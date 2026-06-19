import { Box, Stack, Step, StepConnector, stepConnectorClasses, StepLabel, Stepper, Typography } from '@mui/material'
import { alpha, styled } from '@mui/material/styles'
import type { ReactNode } from 'react'
import PipelineStepIcon from './PipelineStepIcon'
import AgentOrb from './AgentOrb'
import { ShimmerText } from '../motion/primitives'

export interface PipelineStep {
  key: string
  label: string
  icon: ReactNode
}

/**
 * Animated connector between pipeline steps. Completed links are a solid
 * gradient; the in-flight link flows a moving gradient (the "data is moving"
 * cue). Pending links are dim.
 */
const FlowConnector = styled(StepConnector)(({ theme }) => ({
  [`&.${stepConnectorClasses.alternativeLabel}`]: { top: 21 },
  [`& .${stepConnectorClasses.line}`]: {
    height: 3,
    border: 0,
    borderRadius: 3,
    backgroundColor: theme.palette.divider,
    backgroundSize: '200% 100%',
    transition: 'background-color 300ms',
  },
  [`&.${stepConnectorClasses.completed} .${stepConnectorClasses.line}`]: {
    backgroundColor: 'transparent',
    backgroundImage: `linear-gradient(90deg, ${theme.palette.success.main}, ${theme.palette.primary.main})`,
  },
  [`&.${stepConnectorClasses.active} .${stepConnectorClasses.line}`]: {
    backgroundColor: 'transparent',
    backgroundImage: `linear-gradient(90deg, ${theme.palette.primary.main} 0%, ${alpha(theme.palette.primary.main, 0.15)} 50%, ${theme.palette.primary.main} 100%)`,
    animation: 'pipeFlow 1.4s linear infinite',
  },
  '@keyframes pipeFlow': {
    '0%': { backgroundPosition: '200% 0' },
    '100%': { backgroundPosition: '-200% 0' },
  },
}))

/**
 * The animated agent pipeline stepper — extracted from the near-identical
 * inline Stepper blocks in AuthorTestsPage & FindBugsPage. Each page passes its
 * own `steps` + the computed `activeIndex`; presentation lives here.
 */
export default function AgentPipeline({
  steps, activeIndex, inFlight, failed, currentActivity, selectedIndex, onSelect,
}: {
  steps: PipelineStep[]
  activeIndex: number
  inFlight: boolean
  failed: boolean
  currentActivity: string | null
  /** When set (with onSelect), steps become clickable and the selected one is ringed. */
  selectedIndex?: number
  onSelect?: (index: number) => void
}) {
  const clickable = !!onSelect
  return (
    <Box sx={{ mb: 2 }}>
      <Stepper activeStep={activeIndex} alternativeLabel connector={<FlowConnector />}>
        {steps.map((s, i) => {
          const completed = i < activeIndex
          const active = i === activeIndex && !failed
          const running = active && inFlight
          const selected = clickable && i === selectedIndex
          return (
            <Step key={s.key} completed={completed}>
              <StepLabel
                onClick={clickable ? () => onSelect!(i) : undefined}
                sx={clickable ? { cursor: 'pointer', pt: 0.5 } : undefined}
                slots={{
                  stepIcon: () => (
                    <PipelineStepIcon
                      active={active} completed={completed}
                      running={running} failed={failed && i === activeIndex}
                      icon={s.icon}
                    />
                  ),
                }}>
                <Typography variant="body2" sx={{
                  fontWeight: active || selected ? 700 : 500,
                  color: selected ? 'primary.main' : active ? 'primary.main' : completed ? 'success.main' : 'text.secondary',
                  textDecoration: selected ? 'underline' : 'none',
                  textUnderlineOffset: 4,
                }}>
                  {s.label}
                </Typography>
              </StepLabel>
            </Step>
          )
        })}
      </Stepper>

      {currentActivity && (
        <Stack direction="row" spacing={1}
          sx={{ mt: 1.5, alignItems: 'center', justifyContent: 'center' }}>
          {inFlight && <AgentOrb size={20} />}
          {inFlight ? (
            <ShimmerText sx={{ fontSize: '0.875rem' }}>{currentActivity}</ShimmerText>
          ) : (
            <Typography variant="body2" color="text.secondary">
              {currentActivity}
            </Typography>
          )}
        </Stack>
      )}
    </Box>
  )
}
