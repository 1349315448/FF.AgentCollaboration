"""TCP port mapping for the two-node federation test. TLS terminates at the workbench."""
from contextlib import contextmanager
from select import select
import socket
import socketserver
from threading import Thread


@contextmanager
def forward_port(address, port):
    class Forward(socketserver.BaseRequestHandler):
        def handle(self):
            with socket.create_connection((address, port), timeout=5) as upstream:
                upstream.settimeout(None)
                try:
                    while True:
                        readable, _, _ = select([self.request, upstream], [], [])
                        for source in readable:
                            data = source.recv(65536)
                            if not data:
                                return
                            destination = upstream if source is self.request else self.request
                            destination.sendall(data)
                except ConnectionError:
                    return

    with socketserver.ThreadingTCPServer((address, 0), Forward) as server:
        server.daemon_threads = True
        thread = Thread(target=server.serve_forever, daemon=True)
        thread.start()
        try:
            yield server.server_address[1]
        finally:
            server.shutdown()
            thread.join()
