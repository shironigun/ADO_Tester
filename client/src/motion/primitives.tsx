import { animate, motion, useMotionValue, useTransform, type Variants } from 'framer-motion'
import { Box, type BoxProps } from '@mui/material'
import { useEffect, type ReactNode } from 'react'

/**
 * Reusable Framer Motion primitives. Conventions (per the framer-motion skill):
 *   - animate ONLY transform + opacity (GPU); never width/height/margins
 *   - exit animations require AnimatePresence (route transitions in Layout)
 *   - reduced motion honoured globally via <MotionConfig reducedMotion="user">
 *   - motion.div takes no `sx` → wrap with <Box component={motion.div} sx={...}>
 */

const EASE = [0.22, 1, 0.36, 1] as const

// ── page transition (used by Layout's AnimatePresence) ──────────────────────
export const pageVariants: Variants = {
  initial: { opacity: 0, y: 10 },
  animate: { opacity: 1, y: 0, transition: { duration: 0.28, ease: EASE } },
  exit: { opacity: 0, y: -8, transition: { duration: 0.18, ease: EASE } },
}

export function PageTransition({ children }: { children: ReactNode }) {
  return (
    <Box
      component={motion.div}
      variants={pageVariants}
      initial="initial"
      animate="animate"
      exit="exit"
      sx={{ height: '100%' }}
    >
      {children}
    </Box>
  )
}

// ── single fade/slide in ────────────────────────────────────────────────────
export function FadeIn({
  children, delay = 0, y = 12, ...box
}: { children: ReactNode; delay?: number; y?: number } & BoxProps) {
  return (
    <Box
      component={motion.div}
      initial={{ opacity: 0, y }}
      animate={{ opacity: 1, y: 0 }}
      transition={{ duration: 0.4, ease: EASE, delay }}
      {...box}
    >
      {children}
    </Box>
  )
}

// ── staggered list reveal ───────────────────────────────────────────────────
const containerVariants: Variants = {
  hidden: { opacity: 0 },
  visible: { opacity: 1, transition: { staggerChildren: 0.06, delayChildren: 0.04 } },
}
const itemVariants: Variants = {
  hidden: { opacity: 0, y: 12 },
  visible: { opacity: 1, y: 0, transition: { duration: 0.32, ease: EASE } },
}

export function Stagger({ children, ...box }: { children: ReactNode } & BoxProps) {
  return (
    <Box
      component={motion.div}
      variants={containerVariants}
      initial="hidden"
      animate="visible"
      {...box}
    >
      {children}
    </Box>
  )
}

export function StaggerItem({ children, ...box }: { children: ReactNode } & BoxProps) {
  return (
    <Box component={motion.div} variants={itemVariants} {...box}>
      {children}
    </Box>
  )
}

// ── count-up animated number ────────────────────────────────────────────────
export function AnimatedNumber({ value, duration = 0.9 }: { value: number; duration?: number }) {
  const mv = useMotionValue(0)
  const text = useTransform(mv, v => Math.round(v).toLocaleString())
  useEffect(() => {
    const controls = animate(mv, value, { duration, ease: [0.22, 1, 0.36, 1] })
    return () => controls.stop()
  }, [value, duration, mv])
  return <motion.span>{text}</motion.span>
}

// ── streaming shimmer text (a highlight sweeps across the text) ─────────────
export function ShimmerText({ children, ...box }: { children: ReactNode } & BoxProps) {
  return (
    <Box
      component={motion.span}
      animate={{ backgroundPositionX: ['200%', '-200%'] }}
      transition={{ repeat: Infinity, duration: 2.4, ease: 'linear' }}
      sx={theme => ({
        backgroundImage: `linear-gradient(90deg, ${theme.palette.primary.main} 0%, ${theme.palette.secondary.main} 25%, ${theme.palette.primary.main} 50%, ${theme.palette.secondary.main} 75%, ${theme.palette.primary.main} 100%)`,
        backgroundSize: '200% 100%',
        WebkitBackgroundClip: 'text',
        backgroundClip: 'text',
        color: 'transparent',
        fontWeight: 600,
      })}
      {...box}
    >
      {children}
    </Box>
  )
}

// ── pulsing "agent is working" dot ──────────────────────────────────────────
export function PulsingAgentBadge({ size = 10, color = 'primary.main' }: {
  size?: number; color?: string
}) {
  return (
    <Box
      component={motion.span}
      animate={{ scale: [1, 1.45, 1], opacity: [0.55, 1, 0.55] }}
      transition={{ repeat: Infinity, duration: 1.4, ease: 'easeInOut' }}
      sx={{
        display: 'inline-block',
        width: size,
        height: size,
        borderRadius: '50%',
        bgcolor: color,
        boxShadow: theme => `0 0 0 4px ${theme.palette.action.hover}`,
        flexShrink: 0,
      }}
    />
  )
}
