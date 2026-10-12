"""A TCP proxy between a hub and the mod's TCP port, so a case can break the hub's connection without touching the
world (a reconnect, not a world change)."""
import socket
import threading


class Proxy:
    def __init__(self, target_port, host="127.0.0.1"):
        self.target = (host, target_port)
        self.server = socket.socket()
        self.server.bind((host, 0))
        self.server.listen(8)
        self.port = self.server.getsockname()[1]
        self.pairs = []
        self.connections = 0
        self._lock = threading.Lock()
        threading.Thread(target=self._accept, daemon=True).start()

    def _accept(self):
        while True:
            try:
                client, _ = self.server.accept()
            except OSError:
                return
            upstream = socket.create_connection(self.target)
            with self._lock:
                self.pairs.append((client, upstream))
                self.connections += 1
            for a, b in ((client, upstream), (upstream, client)):
                threading.Thread(target=self._pump, args=(a, b), daemon=True).start()

    @staticmethod
    def _pump(src, dst):
        try:
            while True:
                data = src.recv(65536)
                if not data:
                    break
                dst.sendall(data)
        except OSError:
            pass
        for s in (src, dst):
            try:
                s.close()
            except OSError:
                pass

    def sever(self):
        """Drops every live connection at once, both directions."""
        with self._lock:
            pairs, self.pairs = self.pairs, []
        for a, b in pairs:
            for s in (a, b):
                try:
                    s.shutdown(socket.SHUT_RDWR)
                    s.close()
                except OSError:
                    pass

    def close(self):
        self.sever()
        self.server.close()
