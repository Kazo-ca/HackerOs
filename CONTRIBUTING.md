# Contributing to Hacker Simulator

Thank you for your interest in contributing to Hacker Simulator! This document provides guidelines and instructions for contributing to this project.

## Code of Conduct

Please help keep this project open and inclusive. By participating, you agree to:
- Be respectful of different viewpoints and experiences
- Accept constructive criticism gracefully
- Focus on what's best for the community
- Show empathy towards other community members

## How to Contribute

### Reporting Bugs

1. Check if the bug has already been reported in the Issues section
2. Include detailed steps to reproduce the bug
3. Provide information about your environment (browser, OS, etc.)

### Suggesting Features

1. Check if the feature has already been suggested in the Issues section
2. Describe the feature in detail, including why it would be valuable

### Pull Requests

1. Fork the repository
2. Create a new branch for your changes (`git checkout -b feature/amazing-feature`)
3. Make your changes
4. Run tests to ensure your changes don't break existing functionality
5. Commit your changes (`git commit -m 'Add amazing feature'`)
6. Push to the branch (`git push origin feature/amazing-feature`)
7. Open a Pull Request

## Development Guidelines

### Code Style

- Follow the existing code style and naming conventions
- Use TypeScript for new components and features
- Add comments for complex logic
- Ensure your code passes linting checks

### Testing
(Mandatory for now, until we add tests)
- Add tests for new features
- Ensure all tests pass before submitting a pull request
- Include both unit and integration tests where appropriate

### Documentation

- Update documentation when adding or changing features
- Use clear, concise language
- Include code examples where helpful
- Keep the features overview current: a PR to `main` that ships, changes or plans a feature updates [`doc/fr/wasm/features.md`](doc/fr/wasm/features.md) and [`doc/en/wasm/features.md`](doc/en/wasm/features.md) (see the checkbox in the PR template)

## Branching Strategy

- `main` - Production-ready code
- `develop` - Integration branch for features
- `feature/*` - For new features
- `bugfix/*` - For bug fixes
- `release/*` - For release preparation

## Release Process

1. Features are merged into `develop`
2. When ready for release, a `release` branch is created
3. After testing, the release branch is merged into `main` and tagged
4. `main` is then merged back into `develop`

## Getting Help

If you need help with contributing, please:
- Join our community Discord server
- Check the documentation
- Reach out to the maintainers

Thank you for contributing to Hacker Simulator!