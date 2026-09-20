# Security

Report a vulnerability privately through GitHub Security Advisories for the `willibrandon/ilrepl`
repository: open the Security tab and choose "Report a vulnerability". Please do not open a public
issue for one. Fixes go into the latest release.

ilrepl runs the IL you enter, with your permissions. Code that does what you typed is working as
intended. A report is welcome when ilrepl does something you did not ask for, such as running code
while it only completes or analyzes a line, or loading something other than what a session names.

ilrepl does not collect telemetry. It uses the network only to restore packages, for a `nuget:`
dependency or a project you load, and it does that through your own NuGet configuration.
