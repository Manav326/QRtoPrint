# Premium Design System

Premium visual quality is a V1 acceptance requirement across every app and platform, not a later styling phase.

## 1. Product-wide visual principles

- Calm, confident, contemporary and professional; avoid generic admin-template appearance.
- Strong typographic hierarchy, deliberate whitespace, consistent grid and restrained colour.
- Use depth, subtle borders, soft elevation and purposeful accent colour rather than excessive gradients or glass effects.
- Motion should communicate state and improve orientation; respect reduced-motion preferences.
- Every screen must work in light and dark themes with verified contrast.
- Tenant branding can customise logo, name and approved brand accents without breaking readability or layout.

## 2. Shared design tokens

Create central tokens for:
- Colour roles: canvas, surface, elevated surface, text primary/secondary, border, accent, success, warning, danger and info.
- Typography: display, page title, section title, body, label, caption, numeric/data.
- Spacing scale, radii, shadows, focus rings, z-index layers and motion durations.
- Breakpoints and content widths.
- Semantic status colours and accessible text/icon pairing.

Avoid one-off hard-coded colours in application screens. Themes must use semantic tokens.

## 3. Shared components

Buttons, icon buttons, inputs, selects, segmented controls, checkboxes, radio groups, switches, tabs, breadcrumbs, navigation, cards, metric tiles, data tables, filters, status badges, toasts, banners, dialogs, drawers, command/search, empty states, skeletons, charts, pagination, file upload/dropzone, document preview, QR display, printer status and job timeline.

Components must define hover, focus-visible, active, disabled, loading, error and success states as appropriate.

## 4. Application-specific experience

### Customer PWA

Mobile-first and task-focused. The QR session and selected shop/printer must remain visible. Upload, preview, print options, quote and payment should feel like one guided flow. Advanced settings should be collapsible by default. Show page count, colour detection, paper, copies, duplex, estimated amount and clear final confirmation. No confusing admin-style navigation.

### Shop portal/desktop

Desktop-first operations workspace with persistent navigation, clear global status, fast queue search/filter and high information density without visual clutter. Queue status and next action must be obvious. Critical actions require confirmation and audit context. Responsive layouts should remain usable on tablets and smaller laptops.

### Platform administration

Enterprise SaaS control centre with tenant search, subscription health, revenue separation, entitlements, device limits, release management and audit views. Platform-wide and tenant-scoped views must be visually distinct.

### Windows tray/agent

Compact, legible status surface with agent connection, backend sync, printers, active jobs, last error, open shop portal and diagnostics. Do not make the tray UI a miniature copy of the full shop portal.

## 5. Responsive and accessibility acceptance

- Test phone widths, tablet, laptop and large desktop.
- No horizontal overflow in core workflows.
- Touch targets should be comfortable; keyboard navigation and visible focus are mandatory.
- Use semantic HTML, labels, accessible names and screen-reader announcements for asynchronous state changes.
- Meet WCAG AA contrast for text and meaningful controls.
- Respect reduced motion and browser zoom.
- Tables must degrade to usable responsive views, not clipped content.

## 6. Quality gates

Every major screen must be reviewed in both themes and at representative viewport sizes. Review loading, empty, error, offline, permission-denied and success states—not only the happy path. No placeholder controls that look actionable but do nothing. No unstyled browser-default forms in production flows. Capture visual regression baselines once the app shell exists.
