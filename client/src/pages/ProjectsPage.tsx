import { Alert, Box, Card, CardContent, Chip, Stack, Typography } from '@mui/material'

/**
 * Projects page — v1 ships with a single seeded project ("Ideal Agile").
 * Multi-project support lands later; this page documents the current state.
 */
export default function ProjectsPage() {
  return (
    <Box>
      <Typography variant="h5" gutterBottom>Projects</Typography>
      <Stack spacing={2} sx={{ maxWidth: 720 }}>
        <Alert severity="info">
          v1 runs against a single seeded project. Multi-project support is on the roadmap.
        </Alert>
        <Card variant="outlined">
          <CardContent>
            <Stack direction="row" spacing={1} sx={{ alignItems: 'center', mb: 1 }}>
              <Typography variant="h6">Ideal Agile</Typography>
              <Chip size="small" label="Default" color="primary" />
            </Stack>
            <Typography variant="body2" color="text.secondary">
              ADO Organization: <strong>ConstellationDealer</strong>
            </Typography>
            <Typography variant="body2" color="text.secondary">
              ADO Project: <strong>Ideal Agile</strong>
            </Typography>
            <Typography variant="body2" color="text.secondary" sx={{ mt: 1 }}>
              Configured in <code>src/QaAutomation.Api/appsettings.json</code> under
              <code> AzureDevOps </code>. PAT loaded from <code>.env</code>
              (<code>ADOACCESS_PAT</code>).
            </Typography>
          </CardContent>
        </Card>
      </Stack>
    </Box>
  )
}
