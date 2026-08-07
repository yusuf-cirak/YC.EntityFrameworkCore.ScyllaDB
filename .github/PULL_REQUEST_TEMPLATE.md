<!--
The PR title becomes the squashed commit message and drives the release version.
Use Conventional Commits: feat: / fix: / docs: / chore: / refactor: / test:
Breaking change: feat!: or a BREAKING CHANGE: footer.
-->

## What changed

## Why

## CQL impact

<!-- Does this change the generated CQL? Paste a before/after if so. Write "none" otherwise. -->

## Checklist

- [ ] PR title follows Conventional Commits
- [ ] Unit tests cover the change (generated CQL, model validation, or write batching)
- [ ] Functional test added if the change touches the wire
- [ ] New unsupported constructs throw at translation time with an actionable message
- [ ] Public API is documented
