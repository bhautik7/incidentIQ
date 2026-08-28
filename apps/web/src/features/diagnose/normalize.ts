/**
 * The preview's grouping, mirroring `LogMessageNormalizer` on the server.
 *
 * This is what turns a wall of text into four lines the user can check, and it
 * is the moment the tool has to earn trust: if the grouping shown here is
 * visibly wrong, nothing downstream is worth reading.
 *
 * It is an *approximation on purpose*, and the UI says so. The authoritative
 * fingerprint is computed by the event processor from the organization, the
 * environment, the service, the exception type, the normalised message and the
 * top stack frames - four of which this page cannot know. Masking the same
 * shapes in the same order gets the preview to the same answer in practice
 * while leaving the real grouping where it belongs.
 *
 * Order is load-bearing, exactly as it is on the server: every rule below would
 * also match a fragment of the ones above it, so the broad rules must run last.
 */

const RULES: [RegExp, string][] = [
  // Credentials first, and before every broad rule - a JWT contains characters
  // outside [0-9a-f], so the hex rule cannot catch one.
  [/\beyJ[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]{5,}(?:\.[A-Za-z0-9_-]+)?/g, '{TOKEN}'],
  [/\b(token|session|secret|password|pwd|api[_-]?key|apikey|auth|authorization|access[_-]?token|refresh[_-]?token|credential)(\s*[=:]\s*)(?:Bearer\s+)?[^\s,;)\]}{]{6,}/gi, '$1$2{TOKEN}'],
  [/\bBearer\s+[A-Za-z0-9._~+/=-]{12,}/gi, 'Bearer {TOKEN}'],
  [/\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b/g, '{UUID}'],
  [/\d{4}-\d{2}-\d{2}([T ]\d{2}:\d{2}:\d{2}(\.\d+)?(Z|[+-]\d{2}:?\d{2})?)?/g, '{TIMESTAMP}'],
  [/\b[\w.%+-]+@[\w.-]+\.[A-Za-z]{2,}\b/g, '{EMAIL}'],
  [/\b[a-zA-Z][a-zA-Z0-9+.-]*:\/\/[^\s"']+/g, '{URL}'],
  [/\b\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}\b(:\d{1,5})?/g, '{IP}'],
  [/(?:[A-Za-z]:\\|\/)(?:[\w .-]+[/\\])+[\w .-]+/g, '{PATH}'],
  [/\b(0x)?[0-9a-fA-F]{8,}\b/g, '{HEX}'],
  // Deliberately no trailing word boundary: "250ms" and "500ms" are the same
  // failure and must share a template.
  [/-?\b\d[\d,]*(\.\d+)*/g, '{NUM}'],
]

const MAX_LENGTH = 4000

export function normalizeMessage(message: string): string {
  if (!message.trim()) return ''

  let text = message.length > MAX_LENGTH ? message.slice(0, MAX_LENGTH) : message

  for (const [pattern, replacement] of RULES) {
    text = text.replace(pattern, replacement)
  }

  // Collapsed last: indentation differs between runs and is not information.
  return text.replace(/\s+/g, ' ').trim()
}

/**
 * How many stack frames participate in the grouping, mirroring
 * <c>LogFingerprint.StackFrameDepth</c>.
 */
const STACK_FRAME_DEPTH = 3

/**
 * The top frames, stripped of everything that moves: line numbers, file paths,
 * and the "at " prefix.
 *
 * Included in the preview's grouping because it is included in the server's
 * fingerprint, and leaving it out is not a harmless simplification: two lines
 * carrying the same sentence from two different call sites are two patterns
 * downstream, and a preview promising one group would be describing an outcome
 * that is not going to happen.
 *
 * Line numbers are the trap on both sides. Left in, a one-line edit above the
 * throw site forks the pattern.
 */
export function normalizeStackFrames(stackLines: string[]): string {
  return stackLines
    .map((line) => line.trim())
    .filter((line) => line.length > 0)
    .slice(0, STACK_FRAME_DEPTH)
    .map((line) =>
      line
        .replace(/^at\s+/i, '')
        .replace(/\s+in\s+.+?:line\s+\d+/gi, '')
        .replace(/:line\s+\d+/gi, '')
        .trim(),
    )
    .filter((frame) => frame.length > 0)
    .join('\n')
}
