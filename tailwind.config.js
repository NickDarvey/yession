// The Tailwind theme, built locally into a stylesheet (no Play CDN) — the F#-authored
// `Style` values compose these utilities, so `content` scans the F# sources for class
// names. The theme mirrors what `Style.headTags` used to register at runtime.
//
// `safelist` holds the only classes that are ASSEMBLED at runtime (the avatar checkers,
// `Style.checker` in Style.fs) and so never appear as literals for the scanner to find.

/** @type {import('tailwindcss').Config} */
export default {
  content: ['./src/**/*.fs', './app/**/*.fs'],
  safelist: [
    'bg-[conic-gradient(from_0deg,#1ba1e2_25%,#0b5d85_0_50%,#1ba1e2_0_75%,#0b5d85_0)]',
    'bg-[conic-gradient(from_0deg,#a8dd00_25%,#55700a_0_50%,#a8dd00_0_75%,#55700a_0)]',
    'bg-[conic-gradient(from_0deg,#17c3b2_25%,#0a5c54_0_50%,#17c3b2_0_75%,#0a5c54_0)]',
    'bg-[conic-gradient(from_0deg,#4ab8f0_25%,#1a6a96_0_50%,#4ab8f0_0_75%,#1a6a96_0)]',
    'bg-[conic-gradient(from_0deg,#7fb800_25%,#3d5a05_0_50%,#7fb800_0_75%,#3d5a05_0)]',
  ],
  theme: {
    extend: {
      colors: {
        bg: '#000',
        panel: '#0a0a0a',
        surface: '#111',
        'surface-2': '#191919',
        hair: '#1f1f1f',
        ink: '#fff',
        'ink-dim': '#b4b4b4',
        'ink-faint': '#6a6a6a',
        blue: '#1ba1e2',
        green: '#a8dd00',
        err: '#ff4a4a',
      },
      backgroundImage: { grad: 'linear-gradient(180deg,#1ba1e2,#a8dd00)' },
      fontFamily: {
        sans: ['Segoe UI', 'Segoe UI Variable', 'Segoe WP', 'Frutiger', 'Helvetica Neue', 'system-ui', 'sans-serif'],
        mono: ['Cascadia Code', 'Consolas', 'monospace'],
      },
      keyframes: {
        pulse2: { '0%,100%': { opacity: '.25' }, '50%': { opacity: '1' } },
        blink: { '50%': { opacity: '0' } },
      },
      animation: {
        pulse2: 'pulse2 1.1s ease-in-out infinite',
        blink: 'blink 1s steps(1) infinite',
      },
    },
  },
}
