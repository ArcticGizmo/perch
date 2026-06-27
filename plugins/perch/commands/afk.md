---
description: Toggle Perch external (ntfy) notifications for this session
disable-model-invocation: true
---

Handled directly by the perch plugin's UserPromptSubmit hook (`scripts/invoke.ps1`),
which toggles this session's external-notification marker for the tray and reports the result
to you. This body never reaches the model.
