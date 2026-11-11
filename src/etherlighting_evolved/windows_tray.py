"""Windows notification icon for the installed desktop app."""

from uuid import UUID

from pystray._util import win32
from pystray._win32 import Icon

# Explorer associates the user's tray placement with this GUID across launches and upgrades.
TRAY_ICON_GUID = UUID("7982f283-0a47-45ce-9140-36056b57dc94")


class WindowsTrayIcon(Icon):
    def _message(self, code, flags, **kwargs):
        guid = win32.NOTIFYICONDATAW.GUID.from_buffer_copy(TRAY_ICON_GUID.bytes_le)
        return super()._message(code, flags | win32.NIF_GUID, guidItem=guid, **kwargs)
