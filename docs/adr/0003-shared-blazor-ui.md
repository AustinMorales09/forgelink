# ADR 0003: One Razor component library for web and mobile

**Status:** accepted

## Context
Supervisors use browsers, material handlers use Android handhelds, and both need the same inventory, count and trace screens.
Building these twice (Razor pages plus XAML) doubles the work and lets the two versions drift.

## Decision
* Put pages and components in `ForgeLink.UI`, a Razor Class Library that depends only on `ForgeLink.Contracts`.
* Components talk to an `IForgeLinkApi` abstraction:
  * **Blazor Server** binds it to `InProcessForgeLinkApi`, which calls application services directly. Each call creates its own DI scope, because a Blazor circuit lives for hours and must not hold a DbContext, and it attaches the circuit's user for audit and role checks.
  * **MAUI Blazor Hybrid** binds it to the HTTP client wrapped in store-and-forward.
* Live views poll on a timer instead of using a push channel, so the same component behaves the same way over an unreliable network. SignalR remains available for HMI and andon displays that want push.
* Device-specific features stay in the host: native scanning, the scale and the printer in WPF; connectivity events in MAUI.

## Consequences
* One fix lands on every platform at once, and the CSS is shared, with touch-sized targets as the default.
* The WPF station stays native XAML on purpose: it needs serial-port access, global keyboard-wedge focus handling and kiosk behaviour, where WPF is the better tool.
