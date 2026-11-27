"""Exercise two background WebRTC processes without opening a workbench page."""

from contextlib import contextmanager
import json
import os
from pathlib import Path
from queue import Queue
import socket
import socketserver
import subprocess
import sys
import threading


@contextmanager
def bridge(executable):
    with subprocess.Popen([executable], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                          text=True, encoding="utf-8") as process:
        output = Queue()

        def read():
            for line in process.stdout:
                output.put(json.loads(line))
            output.put(None)

        threading.Thread(target=read, daemon=True).start()

        def send(body):
            process.stdin.write(json.dumps(body) + "\n")
            process.stdin.flush()

        def receive():
            result = output.get(timeout=40)
            assert result is not None, process.poll()
            return result

        try:
            yield send, receive
        finally:
            process.stdin.close()
            try:
                process.wait(timeout=5)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait(timeout=5)


class Echo(socketserver.BaseRequestHandler):
    def handle(self):
        self.request.settimeout(10)
        while data := self.request.recv(16_384):
            self.request.sendall(data)


def verify(executable):
    with socketserver.ThreadingTCPServer(("127.0.0.1", 0), Echo) as server:
        server.daemon_threads = True
        handler = threading.Thread(target=server.serve_forever)
        handler.start()
        try:
            with bridge(executable) as (offer, read_provider), bridge(executable) as (answer, read_requester):
                offer({"role": "provider", "federationPort": server.server_address[1]})
                answer({"role": "requester", "offer": read_provider()["sdp"]})
                offer({"Answer": read_requester()["sdp"]})
                port = read_requester()["port"]
                assert read_provider()["ready"]
                for count in (64, 320_000):
                    data = os.urandom(count)
                    with socket.create_connection(("127.0.0.1", port), timeout=12) as client:
                        client.sendall(data)
                        received = bytearray()
                        while len(received) < count:
                            block = client.recv(16_384)
                            assert block, len(received)
                            received.extend(block)
                        assert received == data
            print("PASS two background WebRTC workers, multiple streams and binary round-trip")
        finally:
            server.shutdown()
            handler.join()


if __name__ == "__main__":
    verify(str(Path(sys.argv[1]).resolve()))
