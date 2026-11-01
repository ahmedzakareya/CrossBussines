"""Serves the prototype with caching switched off, so every reload shows the latest files."""
import http.server
import os
import socketserver

PORT = 8765
os.chdir(os.path.dirname(os.path.abspath(__file__)))


class NoCacheHandler(http.server.SimpleHTTPRequestHandler):
    def end_headers(self):
        self.send_header("Cache-Control", "no-store, must-revalidate")
        self.send_header("Expires", "0")
        super().end_headers()


socketserver.TCPServer.allow_reuse_address = True
with socketserver.TCPServer(("", PORT), NoCacheHandler) as httpd:
    print(f"Hospital prototype on http://localhost:{PORT}/index.html")
    httpd.serve_forever()
