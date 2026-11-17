# Contributing

EE has a single author, who writes every change in this repository. Issues and hardware test reports are welcome. Pull requests from other people are not accepted and are closed without review.

EE is MIT licensed, so you may fork it and change your copy.

## Issues

Open an issue with one of the forms on the [issue tracker](https://github.com/Views2k/etherlighting-evolved/issues/new/choose). Search the existing issues first, and report one problem per issue.

A bug report is most useful with:

- the EE version from Settings → About in the desktop app, and your operating system;
- the switch model and firmware version, if a switch is involved;
- what you did, what EE showed (copy messages word for word) and what you expected;
- the relevant lines of `service-diagnostics.log`, described in the [README](README.md#reporting-a-problem).

Leave out passwords, API keys, LAN pairing codes, Tailscale names and public IP addresses. Report security vulnerabilities privately as described in [SECURITY.md](SECURITY.md).

## Hardware reports

Only the USW-Pro-XG-8-PoE has been tested on a physical switch. Reports from other models in the [hardware list](docs/HARDWARE.md), and from firmware versions that the list does not name, help decide what EE can support. A report that EE refused lighting output is useful too.

Use the hardware report form and include the model, the firmware version, the EE version, and what you checked: which Ethernet ports and SFP indicators showed the expected colors, which effects you tried, and any message EE showed. If you run EE from source, add the output of `eectl probe <switch address> -u <SSH username>`, described in the hardware list. It only reads from the switch and prints the switch's host name and model, the state of each port and whether EE allows lighting output on it. Like the app, it saves the switch's SSH key on your computer the first time it connects. Remove the host name if you prefer.

## Feature requests

Open an issue that describes what you want to do. There is no schedule for requests.
