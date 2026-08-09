/** @type {import('tailwindcss').Config} */
module.exports = {
  darkMode: 'class',
  content: [
    './Pages/**/*.cshtml',
    './Shared/**/*.cshtml',
    './_ViewImports.cshtml',
  ],
  theme: {
    extend: {
      fontFamily: {
        heading: ['Oswald', 'sans-serif'],
        mono: ['"Roboto Mono"', 'monospace'],
        sans: ['Raleway', 'sans-serif'],
      },
      colors: {
        neon: {
          cyan: 'var(--neon-cyan)',
          magenta: 'var(--neon-magenta)',
          green: 'var(--neon-green)',
          red: 'var(--neon-red)',
        },
        cyber: {
          card: 'var(--bg-card)',
          surface: 'var(--bg-surface)',
        },
      },
    },
  },
  plugins: [],
};
