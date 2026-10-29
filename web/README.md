# EE web UI

The server serves the contents of this folder at `/`. Put the UI's production build here (with `index.html` at the
top level), or set `static_dir` in `ee.toml` to another folder.

The API is described in [docs/API.md](../docs/API.md). To develop against the simulator without a switch, start
the backend with:

```bash
etherlighting-evolved --demo
```

## Requirements for the UI

- Login: one screen with IP address, SSH username, password, *Remember me* and a Connect button, plus an option
  to log in to the demo. Show the error message from the API when a login fails.
- Ports: show ports 1-8 in their current colors using `preview` from each WebSocket frame. Selecting a port opens
  its color picker and brightness control, its status (link, speed, PoE, traffic), and what set its color
  (`source`).
- Color picker: send `set_port` over the WebSocket on every change, not only when the picker is released.
- Effects: list the 16 effects and generate each effect's controls from its `params`.
- Rules: list rules in order with drag to reorder, an on/off toggle per rule, and adding rules from the templates.
- Disclaimer: show the text from `GET /api/about` on the login screen and an About page.
- Respect `prefers-reduced-motion`, and show a warning before starting the Strobe effect.
