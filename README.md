# NetTrace

**Know exactly when your internet failed, and prove it.**

NetTrace is a lightweight Windows app that quietly watches your network connection in the
background. It records when your connection drops, slows down or becomes unreliable, then
gives you a clear, timestamped report you can send to your internet provider.

It's not a speed test. NetTrace is about reliability over time: the outages at 2 a.m., the
calls that keep dropping, the evenings when everything crawls. Those are the problems that are
hard to describe to support and easy for them to dismiss.

> **Early development.** NetTrace is not ready for everyday use yet. The app opens, but it does
> not monitor your connection yet. The features below describe what version 1.0 will do.

## What NetTrace will do

- **Monitor continuously.** It checks your connection every few seconds from the system tray,
  without getting in your way.
- **Pinpoint where the problem is.** It tells your router or Wi-Fi apart from problems beyond
  your home, and spots DNS failures and sites that fail on their own.
- **Measure connection quality.** It tracks latency, packet loss and jitter, so you can see
  slowdowns as well as full outages.
- **Show live usage in the tray.** Your current download and upload rate is always visible at a
  glance.
- **Keep a daily history.** Browse each day's uptime, incidents and connection quality.
- **Export evidence.** Create JSON and CSV reports for a day or any date range, with a
  checksum so anyone can confirm the report hasn't changed since you exported it.

## Honest by design

NetTrace is careful not to overstate what it knows:

- **Sleep and shutdown are not outages.** Time when your PC was off, asleep or not monitoring
  is shown as a gap. It never counts as downtime.
- **One failed check is not an outage.** Incidents are confirmed only after repeated failures
  across independent checks.
- **No false blame.** Reports describe what was observed, for example "external connectivity
  unavailable while your router responded". They never claim to prove your provider was at
  fault.
- **Missing means missing.** If something can't be measured, NetTrace says so instead of
  showing zero.

## Private by default

- Everything stays on your PC, in `%LOCALAPPDATA%\NetTrace\`.
- No account, no cloud and no telemetry.
- NetTrace never reads your traffic or browsing, and never stores Wi-Fi passwords.
- Before exporting, you can hide your Wi-Fi network name and IP addresses.

## Requirements

Windows 10 or Windows 11 (64-bit).

## Status

NetTrace is in early development. Releases with an installer will be published on this
repository's Releases page once version 1.0 is ready.
