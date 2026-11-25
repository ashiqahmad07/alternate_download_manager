# Security policy

## Reporting a problem

Please don't report security problems in a public issue. Use GitHub's private reporting instead: on the
repository's **Security** tab, choose **Report a vulnerability**. Only the maintainer sees the report.

Include what you can of:

- the ADM version (the setup file's name, or **Settings > Apps > Installed apps**),
- what an attacker can do, and what they need first (a page you visit, a file you download, local
  access),
- the steps or a page that shows it.

You should hear back within a week. Once a fix is released, the report can be made public, with credit to
you if you want it.

## Supported versions

Fixes go into the latest release. Please update before reporting.

## In scope

The app, its browser extension and native-messaging host, the installer, and the scripts in this
repository. Problems in yt-dlp, Deno or ffmpeg belong with their own projects, though a way ADM uses
them unsafely is in scope.
