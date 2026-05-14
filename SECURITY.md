# Security Policy

## Supported Versions

Versions are tagged on GitHub. For this small project, only the latest tagged release line receives security fixes.

| Version | Supported |
| --- | --- |
| Latest GitHub release | Yes |
| Older releases | No |

## Reporting a Vulnerability

Please report vulnerabilities privately through GitHub Security Advisories. If that is not possible, email nicholasmtelliott@gmail.com.

Target acknowledgment time: 48 hours.

Do not disclose the issue publicly until a fix is shipped and a maintainer confirms disclosure timing.

## Security Model

Agents run inside Docker containers by default. Host CLI executors require an explicit `--unsafe` flag. The base `.git` directory is mounted read-only in containers, and the orchestrator owns all git writes.

For details, see `docs/DockerSandbox.md`, `docs/CodexSandbox.md`, `docs/OpenCodeSandbox.md`, `docs/ClaudeQwenSandbox.md`, and `Agent.md`.

This project executes AI-generated code locally. Use it only against repositories the operator controls.
