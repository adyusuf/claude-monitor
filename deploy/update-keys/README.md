# Update keys

The PUBLIC halves of the agent's update-signing keys, one per channel: `test.pub` and `prod.pub` (base64
SubjectPublicKeyInfo, one line). They are public by design; `scripts/build-agent.sh` builds the one named by
`AGENT_UPDATE_CHANNEL` into the agent. The private halves exist only in the maintainer's keychain
(`python3 scripts/sign_manifest.py keygen --channel test|prod`) and are never written to a file or this repository.
See `docs/adr-0004-agent-self-update.md`.
