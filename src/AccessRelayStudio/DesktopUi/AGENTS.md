# Studio UI components

- Use the shared UI exports from `src/ui.tsx` on every screen, including game plugins.
  Primitives live in `src/components/ui/`; follow the component/composition pattern
  in `tuckthomas/cdc-relationship-manager/components`.
- Use Button, Card, Field, Input, Textarea, Checkbox, Slider, Select/SelectOption,
  Tabs, Modal, ActionMenu and Notice. Native form controls and buttons belong only
  inside shared primitives. Purpose-built diagrams and previews compose these
  controls rather than defining another form/menu implementation.
- Use ProfilePicker for a profile selector with an adjacent create action. Call
  controller profiles "Controller profile", including dialogs and actions.
- Use theme tokens for surfaces, control radius, focus, text and spacing. Keep
  text at least 14px. Menus must flip/scroll within available space and work in
  dialogs, with keyboard/typeahead, focus return and disabled options.
- Navbar actions are Beta and the theme toggle. Do not add runtime/status tags,
  activity lights, navigation switches, slogans or emergency action buttons there.
- Marketplace catalog/packages belong in the separate `accessrelay-marketplace`
  repository. The app consumes that catalog and validates the existing pack API.
- Live Gameplay hosts capture and live assistance; AI Model Training hosts recording,
  clip review and offline jobs. Keep game-specific UI in plugins. Detachment reuses
  the native session; do not start another capture or inference worker for a preview.
