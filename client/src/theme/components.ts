import { alpha, type Components, type Theme } from '@mui/material/styles'
import { MONO_FONT } from './tokens'

/**
 * Global component overrides shared by both colour modes. Restyling here keeps
 * the page code untouched while modernising every surface app-wide.
 *
 * NOTE: the `MuiCssBaseline` block re-homes the inline `<code>` styling that
 * used to live in index.css (used by ProjectsPage + KnowledgebasePage). index.css
 * is reduced to a bare reset, so this preserves the chip-like code look.
 */
export const components: Components<Theme> = {
  MuiCssBaseline: {
    styleOverrides: theme => {
      const light = theme.palette.mode === 'light'
      // Atmospheric gradient mesh — fixed so it doesn't scroll. Two coloured
      // radial glows over the base background, tuned per mode.
      const mesh = light
        ? `radial-gradient(900px 600px at 12% -5%, ${alpha(theme.palette.primary.main, 0.10)} 0%, transparent 60%),
           radial-gradient(800px 600px at 105% 10%, ${alpha(theme.palette.secondary.main, 0.08)} 0%, transparent 55%),
           radial-gradient(700px 700px at 80% 110%, ${alpha(theme.palette.info.main, 0.07)} 0%, transparent 60%)`
        : `radial-gradient(1000px 700px at 8% -10%, ${alpha(theme.palette.primary.main, 0.20)} 0%, transparent 55%),
           radial-gradient(900px 700px at 108% 5%, ${alpha(theme.palette.secondary.main, 0.16)} 0%, transparent 50%),
           radial-gradient(800px 800px at 85% 115%, ${alpha(theme.palette.info.main, 0.12)} 0%, transparent 55%)`
      return {
        body: {
          minHeight: '100vh',
          backgroundColor: theme.palette.background.default,
          backgroundImage: mesh,
          backgroundAttachment: 'fixed',
          backgroundRepeat: 'no-repeat',
        },
        'code, .counter': {
          fontFamily: MONO_FONT,
          fontSize: '0.85em',
          padding: '2px 6px',
          borderRadius: 6,
          background: light
            ? alpha(theme.palette.primary.main, 0.08)
            : alpha(theme.palette.primary.main, 0.16),
          color: theme.palette.text.primary,
        },
        '*::-webkit-scrollbar': { width: 10, height: 10 },
        '*::-webkit-scrollbar-thumb': {
          borderRadius: 8,
          background: alpha(theme.palette.text.secondary, 0.3),
        },
        '*::-webkit-scrollbar-thumb:hover': {
          background: alpha(theme.palette.text.secondary, 0.5),
        },
        // Honour reduced-motion for any CSS transitions/animations app-wide.
        '@media (prefers-reduced-motion: reduce)': {
          '*, *::before, *::after': {
            animationDuration: '0.001ms !important',
            animationIterationCount: '1 !important',
            transitionDuration: '0.001ms !important',
          },
        },
      }
    },
  },
  MuiPaper: {
    styleOverrides: { root: { backgroundImage: 'none' } },
  },
  MuiCard: {
    defaultProps: { variant: 'outlined' },
    styleOverrides: {
      root: ({ theme }) => ({
        borderRadius: 16,
        borderColor: theme.palette.divider,
        backgroundColor: theme.palette.mode === 'light'
          ? alpha(theme.palette.background.paper, 0.78)
          : alpha(theme.palette.background.paper, 0.6),
        backdropFilter: 'blur(10px)',
        WebkitBackdropFilter: 'blur(10px)',
        transition: 'border-color 220ms, box-shadow 220ms, transform 220ms cubic-bezier(0.22,1,0.36,1)',
        '&:hover': {
          transform: 'translateY(-2px)',
          borderColor: alpha(theme.palette.primary.main, 0.4),
          boxShadow: theme.palette.mode === 'light'
            ? `0 12px 30px -12px ${alpha(theme.palette.primary.main, 0.28)}`
            : `0 14px 34px -14px ${alpha(theme.palette.common.black, 0.7)}, 0 0 0 1px ${alpha(theme.palette.primary.main, 0.18)}`,
        },
      }),
    },
  },
  MuiButton: {
    defaultProps: { disableElevation: true },
    styleOverrides: {
      root: ({ theme, ownerState }) => {
        const contained = ownerState.variant === 'contained'
        const color = ownerState.color
        const pal = (color === 'primary' || color === 'success' || color === 'secondary' || color === 'error' || color === 'info' || color === 'warning')
          ? theme.palette[color]
          : null
        return {
          textTransform: 'none', borderRadius: 10, fontWeight: 600,
          transition: 'transform 160ms cubic-bezier(0.22,1,0.36,1), box-shadow 200ms, background 200ms',
          '&:active': { transform: 'scale(0.97)' },
          ...(contained && pal && {
            backgroundImage: `linear-gradient(135deg, ${pal.main} 0%, ${pal.dark} 100%)`,
            boxShadow: `0 6px 18px -8px ${alpha(pal.main, 0.6)}`,
            '&:hover': {
              transform: 'translateY(-1px)',
              boxShadow: `0 10px 26px -8px ${alpha(pal.main, 0.7)}`,
            },
            // MUI's disabled state only clears background-color, not the gradient image — reset it
            // so disabled contained buttons read as disabled instead of a faded blue.
            '&.Mui-disabled': { backgroundImage: 'none', boxShadow: 'none' },
          }),
        }
      },
    },
  },
  MuiChip: {
    styleOverrides: { root: { fontWeight: 600, letterSpacing: '0.01em' } },
  },
  MuiTooltip: {
    styleOverrides: {
      tooltip: ({ theme }) => ({
        borderRadius: 8,
        fontSize: '0.75rem',
        background: alpha(theme.palette.grey[900], 0.92),
      }),
    },
  },
  MuiAppBar: {
    styleOverrides: {
      root: ({ theme }) => ({
        backdropFilter: 'blur(8px)',
        backgroundColor: alpha(theme.palette.background.paper, 0.8),
        color: theme.palette.text.primary,
        borderBottom: `1px solid ${theme.palette.divider}`,
        boxShadow: 'none',
      }),
    },
  },
  MuiDrawer: {
    styleOverrides: {
      paper: ({ theme }) => ({
        borderRight: `1px solid ${theme.palette.divider}`,
        backgroundColor: theme.palette.background.paper,
        backgroundImage: 'none',
      }),
    },
  },
}
