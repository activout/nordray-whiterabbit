package passthrough

import (
	"context"
	"crypto/tls"
	"crypto/x509"
	"io"
	"net/http"
	"net/http/httptest"
	"net/url"
	"strings"
	"testing"
	"time"
)

func startStorageForwarder(t *testing.T, upstream http.HandlerFunc) *Forwarder {
	t.Helper()
	server := httptest.NewServer(upstream)
	t.Cleanup(server.Close)
	f, err := StartForwarder(server.URL, func(context.Context) (string, time.Time, error) {
		return "wrk_virtual", time.Now().Add(5 * time.Minute), nil
	}, nil, nil)
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { f.Close() })
	return f
}

// childClient is what BunnyCLI does: HTTPS_PROXY with the local key, NODE_EXTRA_CA_CERTS with the run's CA.
func childClient(t *testing.T, proxy string, roots *x509.CertPool) *http.Client {
	t.Helper()
	proxyURL, err := url.Parse(proxy)
	if err != nil {
		t.Fatal(err)
	}
	return &http.Client{Transport: &http.Transport{Proxy: http.ProxyURL(proxyURL), TLSClientConfig: &tls.Config{RootCAs: roots}}}
}

func TestStorageRequestsGoToWhiteRabbitWithTheVirtualCredential(t *testing.T) {
	var gotPath, gotKey, gotBody string
	f := startStorageForwarder(t, func(w http.ResponseWriter, r *http.Request) {
		gotPath, gotKey = r.URL.EscapedPath(), r.Header.Get("AccessKey")
		body, _ := io.ReadAll(r.Body)
		gotBody = string(body)
		w.WriteHeader(http.StatusCreated)
	})
	roots := x509.NewCertPool()
	roots.AppendCertsFromPEM(f.intercept.caPEM)

	request, _ := http.NewRequest(http.MethodPut, "https://se.storage.bunnycdn.com/sites-x-a1b2c3/deploys/d1/a%20b.html", strings.NewReader("<h1>hi</h1>"))
	request.Header.Set("AccessKey", "[redacted by White Rabbit]") // the placeholder the child was shown
	response, err := childClient(t, f.ProxyURL(), roots).Do(request)
	if err != nil {
		t.Fatal(err)
	}
	response.Body.Close()
	if response.StatusCode != http.StatusCreated {
		t.Fatalf("status %d", response.StatusCode)
	}
	if want := "/proxy/storage/se.storage.bunnycdn.com/sites-x-a1b2c3/deploys/d1/a%20b.html"; gotPath != want {
		t.Errorf("path %q, want %q", gotPath, want)
	}
	if gotKey != "wrk_virtual" {
		t.Errorf("AccessKey %q, want the virtual credential", gotKey)
	}
	if gotBody != "<h1>hi</h1>" {
		t.Errorf("body %q", gotBody)
	}
}

func TestProxyRequiresTheLocalKey(t *testing.T) {
	f := startStorageForwarder(t, func(w http.ResponseWriter, r *http.Request) { t.Error("nothing may be forwarded") })
	roots := x509.NewCertPool()
	roots.AppendCertsFromPEM(f.intercept.caPEM)
	for _, proxy := range []string{f.URL, strings.Replace(f.ProxyURL(), f.LocalKey, "wrong", 1)} {
		if _, err := childClient(t, proxy, roots).Get("https://se.storage.bunnycdn.com/sites-x-a1b2c3/"); err == nil ||
			!strings.Contains(err.Error(), "Proxy Authentication Required") {
			t.Errorf("proxy %s: got %v, want 407", proxy, err)
		}
	}
}

func TestOtherHostsAreTunnelledUntouched(t *testing.T) {
	f := startStorageForwarder(t, func(w http.ResponseWriter, r *http.Request) { t.Error("not for White Rabbit") })
	target := httptest.NewTLSServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		io.WriteString(w, "origin:"+r.Header.Get("AccessKey"))
	}))
	defer target.Close()
	roots := target.Client().Transport.(*http.Transport).TLSClientConfig.RootCAs

	request, _ := http.NewRequest(http.MethodGet, target.URL, nil)
	request.Header.Set("AccessKey", "kept")
	response, err := childClient(t, f.ProxyURL(), roots).Do(request)
	if err != nil {
		t.Fatal(err)
	}
	body, _ := io.ReadAll(response.Body)
	response.Body.Close()
	// The origin's own certificate, and the request exactly as sent: rabbit only intercepts Edge Storage.
	if string(body) != "origin:kept" {
		t.Errorf("got %q", body)
	}
}

func TestRunCAOnlyVouchesForStorageHosts(t *testing.T) {
	i, err := newInterceptor()
	if err != nil {
		t.Fatal(err)
	}
	roots := x509.NewCertPool()
	roots.AddCert(i.caCert)
	for host, ok := range map[string]bool{"se.storage.bunnycdn.com": true, "storage.bunnycdn.com": true, "api.bunny.net": false, "example.com": false} {
		leaf, err := i.leaf(host)
		if err != nil {
			t.Fatal(err)
		}
		cert, _ := x509.ParseCertificate(leaf.Certificate[0])
		_, err = cert.Verify(x509.VerifyOptions{DNSName: host, Roots: roots})
		if (err == nil) != ok {
			t.Errorf("%s: verify error %v, want ok=%v", host, err, ok)
		}
	}
}

func TestParentHTTPSProxy(t *testing.T) {
	if p := parentHTTPSProxy([]string{"https_proxy=proxy.corp:3128"}); p == nil || p.Host != "proxy.corp:3128" {
		t.Errorf("got %v", p)
	}
	if p := parentHTTPSProxy([]string{"HTTP_PROXY=http://x:1"}); p != nil {
		t.Errorf("HTTP_PROXY is not an HTTPS proxy, got %v", p)
	}
}
