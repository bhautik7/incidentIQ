# IncidentIQ UI — design system

The reference frame for every decision below: **an engineer is looking at this
at 03:00, on a laptop, while something is broken.** They are scanning, not
reading. They need to answer "what is broken, since when, and what changed"
in the first five seconds.

That single constraint produces most of the rules here. Density is not an
aesthetic preference — it is what lets someone compare twelve incidents without
scrolling. Restraint is not minimalism — it is what makes a red severity badge
mean something.

---

## 1. Colour

Dark-first, not dark-themed. The palette is built for a dark canvas and light
mode is a later inversion, not a parallel design.

### Neutrals

Four background layers, each a step lighter. Slightly blue-shifted rather than
pure grey — pure grey reads as "unfinished" against coloured status indicators.

| Token | Value | Use |
|---|---|---|
| `canvas` | `#0A0C10` | Page background |
| `surface` | `#101319` | Cards, tables, sidebar |
| `raised` | `#161A21` | Hover, selected rows, inputs |
| `overlay` | `#1B2028` | Popovers, dropdowns, command palette |

| Token | Value | Use |
|---|---|---|
| `border` | `#1E242D` | Default separator — most borders |
| `border-strong` | `#2A323D` | Focused inputs, active tabs |

| Token | Value | Contrast on canvas | Use |
|---|---|---|---|
| `text` | `#E4E9F0` | 15.8:1 | Values, headings |
| `text-muted` | `#98A2B3` | 7.4:1 | Labels, secondary metadata |
| `text-subtle` | `#6B7686` | 4.6:1 | Timestamps, hints — never for anything load-bearing |

All three clear WCAG AA for their sizes. `text-subtle` is deliberately at the
edge and is never used alone to convey meaning.

### Accent

One accent, used sparingly: `#4C8DFF`. Selection, focus rings, links, the
active nav item. Not for decoration, not for KPI numbers, never as a gradient.

An accent that appears everywhere stops indicating anything.

### Severity ramp

| Severity | Colour | Text on dark |
|---|---|---|
| Critical | `#F04438` | `#FF9B94` |
| High | `#F79009` | `#FDB863` |
| Medium | `#EAB308` | `#E8C44D` |
| Low | `#64748B` | `#94A3B8` |

### Status

| Status | Colour |
|---|---|
| Healthy / Resolved | `#12B76A` |
| Degraded / Investigating | `#F79009` |
| Critical / Detected | `#F04438` |
| Unknown / Ignored | `#64748B` |

**Colour is never the only signal.** Every severity and status carries a text
label, and where space is tight, a shape: a filled square for critical, a
half-filled for degraded, a ring for healthy. Roughly 1 in 12 male engineers
has some form of colour-vision deficiency, and red/amber is the exact axis they
lose.

---

## 2. Typography

Two families. No display font, no third weight.

- **UI:** the system sans stack. It renders at 12–13px better than a webfont
  and costs nothing to load.
- **Data:** `ui-monospace, "SF Mono", "JetBrains Mono", Menlo`. Everything that
  is an identifier, a value, or a log line: incident IDs, trace IDs, commit
  SHAs, fingerprints, error messages, numbers in tables.

The sans/mono split is doing real work. In an observability product, *"is this
a label or a value?"* should be answerable without reading.

| Role | Size | Weight | Line height | Notes |
|---|---|---|---|---|
| Page title | 18px | 600 | 1.3 | One per page |
| Section heading | 13px | 600 | 1.4 | Uppercase, `0.04em` tracking, `text-muted` |
| Body / UI default | 13px | 400 | 1.5 | |
| Table cell | 12px | 400 | 1.4 | Mono for values |
| Label / caption | 11px | 500 | 1.4 | Uppercase for column headers |
| KPI value | 28px | 600 | 1.1 | Tabular numerals |

**All numerals are tabular** (`font-variant-numeric: tabular-nums`). Without it,
a column of latencies jitters as it updates and becomes unreadable at a glance.

---

## 3. Spacing and density

4px base unit. The scale stops at 32 — anything larger is a layout gap, not
spacing.

`4 · 6 · 8 · 12 · 16 · 24 · 32`

| Element | Size |
|---|---|
| Top bar height | 48px |
| Sidebar width | 224px expanded / 56px collapsed |
| Table row height | 34px (compact) / 40px (with two lines) |
| Page padding | 20px |
| Card padding | 16px |
| Gap between cards | 12px |

### Radius

`4px` for controls and badges, `6px` for cards and panels. Nothing larger.

12px+ radii read as consumer software. A 4px corner reads as a tool.

### Shadows

Almost none. Depth comes from the four background layers and a 1px border.
Only floating surfaces — dropdowns, the command palette, tooltips — get a
shadow, and a hard one rather than a soft glow.

Soft ambient shadows on cards are the single fastest way to make a dense UI
look like a template.

---

## 4. Status and severity system

One component per concept, used everywhere. Ad-hoc coloured spans are how a
design system dies.

```
SeverityBadge     CRITICAL  HIGH  MEDIUM  LOW
                  ■         ▲     ●       ○     ← shape carries it too

StatusBadge       Detected  Investigating  Resolved  Ignored
                  outlined pill, colour + label

HealthDot         ● Healthy   ◐ Degraded   ■ Critical   ○ Unknown

AIConfidence      87%  ████████░░   High
                  <40% renders "Low — treat as a hint"
```

**Confidence is never shown as a bare percentage.** 87% and 34% look
structurally identical at a glance, so the bar and the word carry the meaning
and the number is the detail.

---

## 5. Information architecture

```
Overview            ← landing. "What is broken right now?"
Incidents           ← the work queue
  └ Incident detail ← THE page. Everything else feeds it.
Services            ← the estate
  └ Service detail
Logs                ← raw evidence, when the aggregates are not enough
Deployments         ← "what changed?"
  └ Deployment detail
AI Investigations   ← explainability and audit
Analytics           ← trends, retrospective
Alert Rules         ← configuration
Team                ← people and roles
Settings            ← org, environments, API keys, retention
```

The hierarchy follows the investigation, not the data model. **Overview →
Incident → Deployment** is the path someone actually walks during an outage;
everything else supports it.

Incident detail is the product. Every other page's job is to get someone there
faster, or to answer a question that page raised.

---

## 6. Navigation

**Sidebar** — persistent, collapsible, three groups separated by a rule:

```
Monitor      Overview · Incidents · Services · Logs
Change       Deployments
Intelligence AI Investigations · Analytics
Configure    Alert Rules · Team · Settings
```

Grouping matters at ten items. Ungrouped, it is a list to read; grouped, it is
four things to recognise.

Active state: left accent bar + raised background + `text` (not muted). Not
colour alone, and not a pill — a pill in a sidebar wastes horizontal space.

**Top bar** — global controls that apply across pages:

`⌘K search · environment · time range · system status · notifications · avatar`

Environment and time range live in the top bar because they are **global
filters**, not page settings. Changing environment on Incidents and having it
reset on Services would be maddening; they belong to the session and persist
in the URL.

---

## 7. Core components

| Component | Responsibility |
|---|---|
| `AppShell` | Sidebar + TopBar + outlet. Owns collapse state. |
| `Sidebar` / `NavItem` | Grouped navigation, active state, collapse. |
| `TopBar` | Global filters and status. |
| `PageHeader` | Title, description, actions. One per page. |
| `MetricCard` | Value, delta, sparkline, label. |
| `SeverityBadge` / `StatusBadge` / `HealthDot` | The status vocabulary. |
| `DataTable` | Sorting, selection, keyboard nav, sticky header, density. |
| `FilterBar` | Filter controls bound to URL params. |
| `TimeRangePicker` / `EnvironmentSelector` | Global filters. |
| `Timeline` | Incident chronology. |
| `AIConfidence` / `EvidenceList` | AI output, honestly presented. |
| `ChartCard` | Chart + title + range, consistent axes and tooltip. |
| `LogRow` | One log line, expandable to JSON. |
| `EmptyState` / `ErrorState` / `Skeleton*` | The three non-happy states. |
| `CommandPalette` | ⌘K navigation and search. |

`DataTable` is the highest-leverage component here: Incidents, Logs,
Deployments, AI Investigations, Services and Team are all the same table with
different columns. Writing it six times is how the UI drifts.

---

## 8. Wireframes

### Overview

```
┌────────────┬──────────────────────────────────────────────────────────────────┐
│ IncidentIQ │ ⌘K Search…        [Production ▾] [24h ▾]  ● Healthy  🔔 3   (BS) │
├────────────┼──────────────────────────────────────────────────────────────────┤
│ MONITOR    │  Production Overview                                             │
│ ▸ Overview │  Last 24 hours · 5 services                                      │
│   Incidents│ ┌──────────┬──────────┬──────────┬──────────┬──────────┐         │
│   Services │ │ ACTIVE   │ ERROR    │ SERVICES │ MTTR     │ AI RUNS  │         │
│   Logs     │ │ INCIDENTS│ EVENTS   │ AFFECTED │          │          │         │
│            │ │    7     │  18.4K   │    3     │  24m     │   12     │         │
│ CHANGE     │ │ ↑3 ▁▃▅▇  │ ↑12% ▃▅▇ │ of 5 ▁▁▃ │ ↓6m ▇▅▃  │ ▁▃▅▇     │         │
│   Deploys  │ └──────────┴──────────┴──────────┴──────────┴──────────┘         │
│            │ ┌──────────────────────────────────────────────────────────────┐ │
│ INTEL      │ │ SYSTEM HEALTH                              error ─ warn ─    │ │
│   AI Inv.  │ │  8% ┤            ╭─╮                                         │ │
│   Analytics│ │     │        ╭───╯ ╰──╮        ▼INC-2391                     │ │
│            │ │  4% ┤   ╭────╯        ╰────╮                                 │ │
│ CONFIGURE  │ │     │───╯                  ╰────────                         │ │
│   Alerts   │ │  0% ┼──┬────┬────┬────┬────┬────┬────┬────                   │ │
│   Team     │ │      ▲v2.13     ▲v2.14                                       │ │
│   Settings │ │     10:00  10:30  11:00  11:30  12:00                        │ │
│            │ └──────────────────────────────────────────────────────────────┘ │
│            │  ACTIVE INCIDENTS                                    View all →  │
├────────────┤ ┌──────────────────────────────────────────────────────────────┐ │
│ Acme Corp ▾│ │ SEV   ID       SERVICE     TITLE          STATUS   DUR   AI  │ │
│ Bhautik S. │ │ ■ CRIT INC-2391 payment-api DB conn exh… Investig  12m  87% │ │
│ ? Help     │ │ ▲ HIGH INC-2387 checkout-a… 500 rate up  Detected   4m  72% │ │
│ ‹ Collapse │ └──────────────────────────────────────────────────────────────┘ │
└────────────┴──────────────────────────────────────────────────────────────────┘
```

### Incident list

```
│  Incidents                                          [Export] [+ Alert rule]   │
│  ┌──────────────────────────────────────────────────────────────────────────┐ │
│  │ 🔍 Search…   [Sev ▾] [Status ▾] [Service ▾] [Env ▾] [24h ▾] [AI ≥ ▾]  ⟲ │ │
│  └──────────────────────────────────────────────────────────────────────────┘ │
│  3 of 47 · filtered by severity: critical                        Clear all    │
│ ┌───────────────────────────────────────────────────────────────────────────┐ │
│ │ ☐ SEV    ID       TITLE              SERVICE   ENV  STATUS   START  DUR  AI│
│ ├───────────────────────────────────────────────────────────────────────────┤ │
│ │ ☐ ■ CRIT INC-2391 DB conn exhaustion  payment  prod Investig  10:39  26m 87│
│ │ ☐ ▲ HIGH INC-2387 Elevated 500 rate   checkout prod Detected  10:52  13m 72│
│ │ ☐ ● MED  INC-2379 Kafka consumer lag  notify   prod Detected  09:14 1h51 44│
│ └───────────────────────────────────────────────────────────────────────────┘ │
│                                              ‹ Prev   1–25 of 47   Next ›     │
```

### Incident detail — the important one

```
│ ← Incidents                                                                   │
│ ■ CRITICAL   Investigating   Production                                       │
│ INC-2391  Payment API database connection exhaustion                          │
│ payment-api · started 10:39 · 26m · owner Sarah Chen                          │
│ [Acknowledge] [Assign ▾] [Resolve] [Run AI analysis] [+ Note]                 │
├───────────────────────────────────────┬───────────────────────────────────────┤
│ AI INVESTIGATION            87% ████▊ │ TIMELINE                              │
│                                       │                                       │
│ Probable cause                        │ ○ 10:31  ⬆ Deploy payment-api v2.14   │
│ Database connection pool exhaustion   │ │                                     │
│ introduced by connection disposal     │ ● 10:37  ⚠ DB timeouts begin          │
│ change in v2.14.                      │ │                                     │
│                                       │ ● 10:38  ↑ Error rate +640%           │
│ Evidence                              │ │                                     │
│ • ConnectionPoolTimeout ↑840%         │ ◆ 10:39  ⚑ INC-2391 created           │
│ • v2.14 deployed 8m before onset      │ │                                     │
│ • 94% similarity with INC-183         │ ● 10:39  ◇ AI investigation requested │
│ • Fingerprint seen 18,428×            │ │                                     │
│                                       │ ● 10:40  ⟲ INC-183 retrieved (94%)    │
│ Recommended actions                   │ │                                     │
│ 1. Inspect connection disposal in…    │ ● 10:40  ◇ Analysis completed         │
│ 2. Diff PaymentRepository v2.13→14    │                                       │
│ 3. Review active PG connections       ├───────────────────────────────────────┤
│ 4. Consider rollback if rate persists │ RELATED DEPLOYMENT                    │
│                                       │ payment-api v2.14   a83cb9f           │
│ ⚠ AI-generated. Verify before acting  │ Deployed 10:31 · incident +8m         │
│   on production.                      │ [View deployment →]                   │
├───────────────────────────────────────┼───────────────────────────────────────┤
│ ERROR PATTERN                         │ SIMILAR INCIDENTS                     │
│ Connection timeout while acquiring    │ INC-183  DB pool exhaustion      94%  │
│ PostgreSQL connection                 │          resolved in 31m              │
│ 18,428 occurrences                    │ INC-921  PostgreSQL timeout      81%  │
│ first 10:37 · last 10:52              │          resolved in 22m              │
│ [▸ Show sample raw logs]              │                                       │
└───────────────────────────────────────┴───────────────────────────────────────┘
```

Two columns because the two questions are asked together: *what does the
system think happened* (left) and *what actually happened, in order* (right).
Forcing a scroll between them is the difference between a tool and a report.

### Log explorer

```
│  Logs                                              ● Live tail  [⏸ Pause]     │
│ ┌───────────────────────────────────────────────────────────────────────────┐ │
│ │ [Service ▾][Env ▾][Severity ▾][24h ▾]  🔍 message…  trace:  fingerprint:  │ │
│ └───────────────────────────────────────────────────────────────────────────┘ │
│ ┌───────────────────────────────────────────────────────────────────────────┐ │
│ │ TIMESTAMP      SEV   SERVICE     MESSAGE                 TRACE    INCIDENT│ │
│ ├───────────────────────────────────────────────────────────────────────────┤ │
│ │ 10:42:19.283  ERROR payment-api  Timeout acquiring PG…  abc-123  INC-2391 │ │
│ │ ▾ 10:42:19.104 ERROR payment-api Timeout acquiring PG…  abc-122  INC-2391 │ │
│ │   ┌─────────────────────────────────────────────────────────────────────┐ │ │
│ │   │ {  "traceId": "abc-122",  "pod": "payment-api-7d9f",  … }  [Copy]   │ │ │
│ │   │ Fingerprint 256166cd11…  [Filter by this]  [Open INC-2391]          │ │ │
│ │   └─────────────────────────────────────────────────────────────────────┘ │ │
│ │ 10:42:18.991 WARN  payment-api  Pool at 82% utilisation  abc-121      —   │ │
│ └───────────────────────────────────────────────────────────────────────────┘ │
│                                                    Load older ↓  (cursor)     │
```

---

## 9. Why this reads as a real observability platform

- **Density is the tell.** Real tools show 25 rows without scrolling. A 34px
  row at 12px mono is the difference between scanning and paging.
- **Monospace on every identifier.** Trace IDs, SHAs and fingerprints are
  compared character by character; proportional type makes that hard, and its
  absence is instantly recognisable as "someone who has not used one of these".
- **Time is a first-class axis.** Deployment markers on the health chart,
  relative durations everywhere, a global time range. Incidents are events in
  time, not records in a list.
- **Correlation is surfaced, not buried.** "v2.14 deployed 8 minutes before"
  is the highest-value sentence in the product and it sits at the top.
- **Everything is filterable and linkable.** Filters live in the URL, so a
  filtered view can be pasted into an incident channel.
- **The AI is presented as evidence, not verdict.** Confidence, the evidence
  it used, and a warning. A tool that overstates certainty gets ignored after
  the first wrong call.
- **Restraint.** One accent, near-zero shadow, no gradients. The only saturated
  colour on screen is a status.

## 10. What would make it look amateurish — and the rule against each

| Pattern | Why it fails here | Rule |
|---|---|---|
| Huge hero sections | Wastes the fold; nothing to sell — the user is already inside | Page header ≤ 64px |
| Gradient cards, glassmorphism | Reduces contrast, dates instantly, competes with status colour | No gradients. Flat surfaces + 1px borders |
| 16px+ radii, big soft shadows | Consumer-app language; makes dense tables look like toys | 4–6px radius; shadows only on floating surfaces |
| Oversized padding | Halves rows-per-screen | 34px rows, 16px card padding |
| Colour-only severity | Excludes colour-blind users; illegible on projectors | Always colour + text (+ shape when tight) |
| Centred page spinners | Layout jumps; hides structure | Skeletons matching final layout |
| "No data." | Wastes a moment that could teach | Explain state + offer the next action |
| Emoji as status icons | Renders inconsistently; reads as unserious | Line icons from one set |
| Animated everything | Motion means "look here"; constant motion means nothing | Transitions ≤120ms, on hover/focus only |
| Many accent colours | Nothing stands out when everything does | One accent; colour reserved for status |
| Proportional numerals | Columns jitter on refresh | `tabular-nums` everywhere |
| Modal-per-action | Breaks flow during an incident | Inline panels and side drawers |
| Fake integrations | Erodes trust in everything else on the page | Disabled + "Coming later" |
