"""WebSocket transport selection for the local API server."""

from uvicorn.protocols.websockets.auto import AutoWebSocketsProtocol


def websocket_protocol():
    if AutoWebSocketsProtocol is None:
        return "none"
    if AutoWebSocketsProtocol.__module__ != "uvicorn.protocols.websockets.websockets_sansio_impl":
        return AutoWebSocketsProtocol

    from websockets.protocol import CLOSED, CLOSING

    class ClosingWebSocketProtocol(AutoWebSocketsProtocol):
        def shutdown(self) -> None:
            # A peer close can advance the protocol before Uvicorn marks close_sent.
            if self.handshake_complete and self.conn.state in (CLOSING, CLOSED):
                self.close_sent = True
            super().shutdown()

    return ClosingWebSocketProtocol
