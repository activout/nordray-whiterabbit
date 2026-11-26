package passthrough

import (
	"bufio"
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/rand"
	"crypto/subtle"
	"crypto/tls"
	"crypto/x509"
	"crypto/x509/pkix"
	"encoding/base64"
	"encoding/pem"
	"errors"
	"fmt"
	"io"
	"math/big"
	"net"
	"net/http"
	"net/http/httputil"
	"net/url"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"time"
)

// storageHosts are Bunny Edge Storage's HTTP API hosts, one per region (the list @bunny.net/storage-sdk uses).
// BunnyCLI connects to them itself with a storage zone's password, e.g. for `bunny sites deploy`; there is no
// setting to point it elsewhere. rabbit is the child's HTTPS proxy and terminates TLS for these hosts only, with a
// certificate from a CA that exists for this one run, and sends the requests to White Rabbit, which injects the
// zone's real password. The child only ever holds White Rabbit's placeholder for it.
var storageHosts = map[string]bool{
	"storage.bunnycdn.com": true, "uk.storage.bunnycdn.com": true, "ny.storage.bunnycdn.com": true,
	"la.storage.bunnycdn.com": true, "sg.storage.bunnycdn.com": true, "se.storage.bunnycdn.com": true,
	"br.storage.bunnycdn.com": true, "jh.storage.bunnycdn.com": true, "syd.storage.bunnycdn.com": true,
}

// interceptor holds the run's CA and the certificates it issued.
type interceptor struct {
	caCert *x509.Certificate
	caKey  *ecdsa.PrivateKey
	caPEM  []byte

	mu     sync.Mutex
	leaves map[string]*tls.Certificate
}

func newInterceptor() (*interceptor, error) {
	key, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if err != nil {
		return nil, err
	}
	now := time.Now()
	template := &x509.Certificate{
		SerialNumber:          randomSerial(),
		Subject:               pkix.Name{CommonName: "rabbit one-run storage CA"},
		NotBefore:             now.Add(-time.Minute),
		NotAfter:              now.Add(24 * time.Hour),
		IsCA:                  true,
		BasicConstraintsValid: true,
		MaxPathLenZero:        true,
		KeyUsage:              x509.KeyUsageCertSign,
		// The CA can only ever vouch for the storage hosts, even if its key leaked from this process.
		PermittedDNSDomainsCritical: true,
		PermittedDNSDomains:         []string{"storage.bunnycdn.com"},
	}
	der, err := x509.CreateCertificate(rand.Reader, template, template, &key.PublicKey, key)
	if err != nil {
		return nil, err
	}
	cert, err := x509.ParseCertificate(der)
	if err != nil {
		return nil, err
	}
	return &interceptor{
		caCert: cert,
		caKey:  key,
		caPEM:  pem.EncodeToMemory(&pem.Block{Type: "CERTIFICATE", Bytes: der}),
		leaves: map[string]*tls.Certificate{},
	}, nil
}

func (i *interceptor) leaf(host string) (*tls.Certificate, error) {
	i.mu.Lock()
	defer i.mu.Unlock()
	if c, ok := i.leaves[host]; ok {
		return c, nil
	}
	key, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if err != nil {
		return nil, err
	}
	now := time.Now()
	template := &x509.Certificate{
		SerialNumber: randomSerial(),
		Subject:      pkix.Name{CommonName: host},
		DNSNames:     []string{host},
		NotBefore:    now.Add(-time.Minute),
		NotAfter:     now.Add(24 * time.Hour),
		KeyUsage:     x509.KeyUsageDigitalSignature,
		ExtKeyUsage:  []x509.ExtKeyUsage{x509.ExtKeyUsageServerAuth},
	}
	der, err := x509.CreateCertificate(rand.Reader, template, i.caCert, &key.PublicKey, i.caKey)
	if err != nil {
		return nil, err
	}
	c := &tls.Certificate{Certificate: [][]byte{der}, PrivateKey: key}
	i.leaves[host] = c
	return c, nil
}

// writeCAFile writes the CA for the child's NODE_EXTRA_CA_CERTS, after any CAs the parent already trusted that way.
func (i *interceptor) writeCAFile(parentExtra string) (dir, file string, err error) {
	dir, err = os.MkdirTemp("", "rabbit-ca-")
	if err != nil {
		return "", "", err
	}
	content := []byte{}
	if parentExtra != "" {
		if existing, err := os.ReadFile(parentExtra); err == nil {
			content = append(append(content, existing...), '\n')
		}
	}
	file = filepath.Join(dir, "ca.pem")
	if err := os.WriteFile(file, append(content, i.caPEM...), 0o600); err != nil {
		os.RemoveAll(dir)
		return "", "", err
	}
	return dir, file, nil
}

// proxyAuthorized checks the child's proxy credentials: user "rabbit", password the one-time local key.
func (f *Forwarder) proxyAuthorized(r *http.Request) bool {
	user, pass, ok := parseBasic(r.Header.Get("Proxy-Authorization"))
	return ok && user == "rabbit" && subtle.ConstantTimeCompare([]byte(pass), []byte(f.LocalKey)) == 1
}

// handleConnect serves the child's CONNECT: storage hosts are intercepted, everything else is tunnelled unchanged
// (through the parent's own HTTPS proxy when it had one), e.g. BunnyCLI's update check or a site's live URL.
func (f *Forwarder) handleConnect(w http.ResponseWriter, r *http.Request) {
	if !f.proxyAuthorized(r) {
		w.Header().Set("Proxy-Authenticate", `Basic realm="rabbit"`)
		http.Error(w, "rabbit: only the bunny process started by rabbit may use this proxy", http.StatusProxyAuthRequired)
		return
	}
	host, _, err := net.SplitHostPort(r.Host)
	if err != nil {
		http.Error(w, "rabbit: bad CONNECT target", http.StatusBadRequest)
		return
	}
	host = strings.ToLower(host)

	var upstream net.Conn
	if !storageHosts[host] {
		if upstream, err = f.dialTunnel(r.Host); err != nil {
			f.logf("rabbit: tunnel to %s failed: %v", r.Host, err)
			http.Error(w, "rabbit: cannot connect", http.StatusBadGateway)
			return
		}
	}
	hijacker, ok := w.(http.Hijacker)
	if !ok {
		http.Error(w, "rabbit: cannot tunnel", http.StatusInternalServerError)
		return
	}
	conn, buffered, err := hijacker.Hijack()
	if err != nil {
		return
	}
	if _, err := conn.Write([]byte("HTTP/1.1 200 Connection established\r\n\r\n")); err != nil {
		conn.Close()
		return
	}
	if upstream != nil {
		go pipe(upstream, conn, buffered.Reader)
		return
	}
	f.serveStorage(host, &bufferedConn{Conn: conn, r: buffered.Reader})
}

// serveStorage answers the child's TLS connection to a storage host with requests forwarded to White Rabbit.
func (f *Forwarder) serveStorage(host string, conn net.Conn) {
	certificate, err := f.intercept.leaf(host)
	if err != nil {
		f.logf("rabbit: certificate for %s: %v", host, err)
		conn.Close()
		return
	}
	tlsConn := tls.Server(conn, &tls.Config{Certificates: []tls.Certificate{*certificate}, NextProtos: []string{"http/1.1"}, MinVersion: tls.VersionTLS12})
	proxy := &httputil.ReverseProxy{
		Rewrite: func(pr *httputil.ProxyRequest) {
			target := *f.upstream
			target.Path = "/proxy/storage/" + host + pr.In.URL.Path
			target.RawPath = "/proxy/storage/" + host + pr.In.URL.EscapedPath()
			target.RawQuery = pr.In.URL.RawQuery
			pr.Out.URL = &target
			pr.Out.Host = f.upstream.Host
			for _, h := range []string{"AccessKey", "Authorization", "Cookie", "Proxy-Authorization", "X-Remote-User", "X-Remote-Groups", "X-Remote-Extra"} {
				pr.Out.Header.Del(h)
			}
			pr.Out.Header.Set("AccessKey", pr.In.Context().Value(credentialKey{}).(string))
		},
		Transport:      f.transport,
		ErrorHandler:   f.proxyError,
		ModifyResponse: f.logResponse,
	}
	handler := http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		key, err := f.credential(r.Context())
		if err != nil {
			f.logf("rabbit: %v", err)
			writeJSON(w, http.StatusUnauthorized, `{"error":"rabbit_no_credential"}`)
			return
		}
		proxy.ServeHTTP(w, r.WithContext(contextWithCredential(r.Context(), key)))
	})
	server := &http.Server{Handler: handler, ReadHeaderTimeout: 30 * time.Second}
	_ = server.Serve(&oneConnListener{conn: tlsConn})
}

// dialTunnel connects to target directly, or through the parent's HTTPS proxy with a CONNECT of its own. An
// https:// proxy is spoken to over TLS (port 443 by default), an http:// one in plain text (port 80 by default).
func (f *Forwarder) dialTunnel(target string) (net.Conn, error) {
	if f.parentProxy == nil {
		return net.DialTimeout("tcp", target, 15*time.Second)
	}
	secure := f.parentProxy.Scheme == "https"
	port := f.parentProxy.Port()
	if port == "" {
		port = map[bool]string{true: "443", false: "80"}[secure]
	}
	addr := net.JoinHostPort(f.parentProxy.Hostname(), port)
	dialer := &net.Dialer{Timeout: 15 * time.Second}
	var conn net.Conn
	var err error
	if secure {
		conn, err = tls.DialWithDialer(dialer, "tcp", addr, &tls.Config{ServerName: f.parentProxy.Hostname(), RootCAs: f.parentProxyRoots, MinVersion: tls.VersionTLS12})
	} else {
		conn, err = dialer.Dial("tcp", addr)
	}
	if err != nil {
		return nil, err
	}
	request := "CONNECT " + target + " HTTP/1.1\r\nHost: " + target + "\r\n"
	if u := f.parentProxy.User; u != nil {
		pass, _ := u.Password()
		request += "Proxy-Authorization: Basic " + base64.StdEncoding.EncodeToString([]byte(u.Username()+":"+pass)) + "\r\n"
	}
	if _, err := io.WriteString(conn, request+"\r\n"); err != nil {
		conn.Close()
		return nil, err
	}
	reader := bufio.NewReader(conn)
	response, err := http.ReadResponse(reader, &http.Request{Method: http.MethodConnect})
	if err != nil || response.StatusCode != http.StatusOK {
		conn.Close()
		if err == nil {
			err = fmt.Errorf("proxy %s answered %s", f.parentProxy.Host, response.Status)
		}
		return nil, err
	}
	return &bufferedConn{Conn: conn, r: reader}, nil
}

// parentHTTPSProxy is the parent's HTTPS proxy, which tunnels that rabbit does not intercept still go through. A
// value without a scheme is http://; http and https proxies are supported, anything else (e.g. socks5) is refused
// rather than silently bypassed.
func parentHTTPSProxy(environ []string) (*url.URL, error) {
	for _, name := range []string{"HTTPS_PROXY", "https_proxy"} {
		for _, kv := range environ {
			value, ok := strings.CutPrefix(kv, name+"=")
			if !ok || value == "" {
				continue
			}
			if !strings.Contains(value, "://") {
				value = "http://" + value
			}
			u, err := url.Parse(value)
			if err != nil || u.Hostname() == "" {
				return nil, fmt.Errorf("%s is not a proxy URL rabbit understands", name)
			}
			if u.Scheme != "http" && u.Scheme != "https" {
				return nil, fmt.Errorf("%s uses %s://, which rabbit does not support; use an http:// or https:// proxy", name, u.Scheme)
			}
			return u, nil
		}
	}
	return nil, nil
}

// withoutStorageBypass removes NO_PROXY entries that would let the child reach an Edge Storage host directly: "*",
// and domains such as "bunnycdn.com", ".bunnycdn.com" or "*.storage.bunnycdn.com". The child would then send White
// Rabbit's placeholder as the zone password. Every other entry is kept.
func withoutStorageBypass(noProxy string) string {
	var kept []string
	for _, entry := range strings.Split(noProxy, ",") {
		entry = strings.TrimSpace(entry)
		if entry == "" || bypassesStorage(entry) {
			continue
		}
		kept = append(kept, entry)
	}
	return strings.Join(kept, ",")
}

func bypassesStorage(entry string) bool {
	e := strings.ToLower(entry)
	if e == "*" {
		return true
	}
	if host, _, err := net.SplitHostPort(e); err == nil {
		e = host
	}
	e = strings.TrimPrefix(strings.TrimPrefix(e, "*"), ".")
	if e == "" {
		return false
	}
	for host := range storageHosts {
		if host == e || strings.HasSuffix(host, "."+e) {
			return true
		}
	}
	return false
}

func pipe(upstream, client net.Conn, buffered io.Reader) {
	defer upstream.Close()
	defer client.Close()
	go func() { _, _ = io.Copy(upstream, buffered) }()
	_, _ = io.Copy(client, upstream)
}

func parseBasic(header string) (user, pass string, ok bool) {
	encoded, found := strings.CutPrefix(header, "Basic ")
	if !found {
		return "", "", false
	}
	decoded, err := base64.StdEncoding.DecodeString(encoded)
	if err != nil {
		return "", "", false
	}
	user, pass, ok = strings.Cut(string(decoded), ":")
	return user, pass, ok
}

func randomSerial() *big.Int {
	n, err := rand.Int(rand.Reader, new(big.Int).Lsh(big.NewInt(1), 120))
	if err != nil {
		panic(err)
	}
	return n
}

// bufferedConn reads what the HTTP server already buffered before the connection was hijacked.
type bufferedConn struct {
	net.Conn
	r io.Reader
}

func (c *bufferedConn) Read(p []byte) (int, error) { return c.r.Read(p) }

// oneConnListener hands http.Server a single connection; the server keeps serving it after Accept fails.
type oneConnListener struct {
	conn net.Conn
	once sync.Once
}

func (l *oneConnListener) Accept() (net.Conn, error) {
	var c net.Conn
	l.once.Do(func() { c = l.conn })
	if c == nil {
		return nil, errors.New("rabbit: single connection already accepted")
	}
	return c, nil
}

func (l *oneConnListener) Close() error   { return nil }
func (l *oneConnListener) Addr() net.Addr { return l.conn.LocalAddr() }
