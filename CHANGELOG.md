# Changelog

## 0.4.19

- Switch health displays the three temperature channels reported by the Pro XG 8 PoE.
- Self-hosted controller sign-in uses the same strict login option as UniFi Network. EE renews an expired controller session when UniFi reports it in a JSON response.
- Controller service errors in Settings identify the failed operation and show the HTTP status when available.

## 0.4.18

- Hide Port controls to expand the map. Reopen the panel to continue editing the same selection.
- The touchscreen color editor keeps the color and hue controls open until Done. Apply changes sends the preview to the switch.
- Uplink bandwidth shows Download first, followed by Upload.

## 0.4.17

- The rack port inspector uses the full workspace height. The map key and brightness control share a row with the link legend.
- Uplink rates stay in Mbps across gigabit speeds. The rack display shows the last minute of measured traffic and the highest five-second reading for each direction.

## 0.4.16

- The Lighting faceplate shows uplink bandwidth next to the switch name. The eight Ethernet ports and two SFP ports share a row on desktop and rack displays.
- Default brightness stays within reach while editing. The map key includes the full link-speed legend and brightness help.
- The window header and workspace toolbar use less vertical space.

## 0.4.15

Changes since 0.4.12:

- Compact rack and phone layouts retain port selection, groups, profiles, and Apply controls.
- The port inspector scrolls independently under the pointer, without a second visible scrollbar.
- Color editing uses a shared touch picker in Lighting and Automation.
- Drafts retain edits across port selections and can be saved as preview profiles before applying.
- Apply feedback identifies matching automation rules and links to their controls.
- Settings groups connection controls consistently and clarifies saved-login preferences.
- Validation, cancel navigation, profile Undo, and synchronized SFP previews preserve editing context.
- Repository verification records source inputs, test results, and packaged simulator checks. Windows CI produces installers for verified main revisions.
