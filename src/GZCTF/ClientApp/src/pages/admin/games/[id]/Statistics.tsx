import {
  Alert,
  Button,
  Card,
  Group,
  SimpleGrid,
  Skeleton,
  Stack,
  Table,
  Text,
  Title,
} from '@mantine/core'
import { mdiDownload } from '@mdi/js'
import { Icon } from '@mdi/react'
import type { EChartsOption } from 'echarts'
import { useMemo, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { useParams } from 'react-router'
import { WithGameEditTab } from '@Components/admin/WithGameEditTab'
import { EchartsContainer } from '@Components/charts/EchartsContainer'
import api, { ChallengeStatisticModel, ChallengeType } from '@Api'
import { downloadBlob } from '@Utils/ApiHelper'
import { useAdminGame, useAdminChallengeStatistics } from '@Hooks/useGame'

const formatPercentage = (value?: number | null) =>
  value !== undefined && value !== null ? `${(value * 100).toFixed(2)}%` : '—'

const formatNumber = (value?: number | null, fractionDigits = 2) =>
  value !== undefined && value !== null ? Number.parseFloat(value.toFixed(fractionDigits)).toString() : '—'

const resolveMetric = (metric: ChallengeStatisticModel['attemptsToSolve'], key: keyof NonNullable<ChallengeStatisticModel['attemptsToSolve']>) =>
  metric?.[key] ?? null

const resolveSolveMetric = (
  metric: ChallengeStatisticModel['solveTimeMinutes'],
  key: keyof NonNullable<ChallengeStatisticModel['solveTimeMinutes']>,
) => metric?.[key] ?? null

const hasSolveTimeData = (stat: ChallengeStatisticModel) =>
  stat.type === ChallengeType.DynamicContainer &&
  (stat.solveTimeMinutes?.average !== undefined && stat.solveTimeMinutes?.average !== null)

export const GameStatisticsPage = () => {
  const { id } = useParams()
  const numId = Number(id)
  const { t } = useTranslation()

  const { game } = useAdminGame(numId)
  const { statistics, error } = useAdminChallengeStatistics(numId)
  const [downloading, setDownloading] = useState(false)

  const normalized = statistics ?? []
  const labels = useMemo(
    () =>
      normalized.map((stat) =>
        stat.title && stat.title.trim().length > 0
          ? stat.title
          : t('admin.content.games.statistics.labels.untitled'),
      ),
    [normalized, t],
  )

  const completionOption = useMemo<EChartsOption>(() => {
    if (normalized.length === 0)
      return {
        xAxis: { type: 'category', data: [] },
        yAxis: { type: 'value', min: 0, max: 1 },
        series: [],
      }

    const data = normalized.map((stat) => stat.completionRate ?? 0)

    return {
      tooltip: {
        trigger: 'axis',
        valueFormatter: (value) => `${(Number(value) * 100).toFixed(2)}%`,
      },
      xAxis: {
        type: 'category',
        data: labels,
        axisLabel: { interval: 0, rotate: labels.length > 6 ? 30 : 0 },
      },
      yAxis: {
        type: 'value',
        min: 0,
        max: 1,
        axisLabel: {
          formatter: (value: number) => `${(value * 100).toFixed(0)}%`,
        },
      },
      series: [
        {
          name: t('admin.content.games.statistics.charts.completion'),
          type: 'bar',
          data,
          emphasis: { focus: 'series' },
        },
      ],
    }
  }, [labels, normalized, t])

  const attemptsOption = useMemo<EChartsOption>(() => {
    if (normalized.length === 0)
      return {
        xAxis: { type: 'category', data: [] },
        yAxis: { type: 'value', min: 0 },
        series: [],
      }

    return {
      tooltip: {
        trigger: 'axis',
      },
      xAxis: {
        type: 'category',
        data: labels,
        axisLabel: { interval: 0, rotate: labels.length > 6 ? 30 : 0 },
      },
      yAxis: {
        type: 'value',
        min: 0,
      },
      series: [
        {
          name: t('admin.content.games.statistics.charts.attempts'),
          type: 'bar',
          data: normalized.map((stat) => resolveMetric(stat.attemptsToSolve, 'average') ?? 0),
          emphasis: { focus: 'series' },
        },
      ],
    }
  }, [labels, normalized, t])

  const dynamicStats = useMemo(
    () => normalized.filter((stat) => hasSolveTimeData(stat)),
    [normalized],
  )

  const dynamicLabels = useMemo(
    () =>
      dynamicStats.map((stat) =>
        stat.title && stat.title.trim().length > 0
          ? stat.title
          : t('admin.content.games.statistics.labels.untitled'),
      ),
    [dynamicStats, t],
  )

  const solveTimeAverageOption = useMemo<EChartsOption>(() => {
    if (dynamicStats.length === 0)
      return {
        xAxis: { type: 'category', data: [] },
        yAxis: { type: 'value', min: 0 },
        series: [],
      }

    return {
      tooltip: {
        trigger: 'axis',
        valueFormatter: (value) => `${Number(value).toFixed(2)} min`,
      },
      xAxis: {
        type: 'category',
        data: dynamicLabels,
        axisLabel: { interval: 0, rotate: dynamicLabels.length > 6 ? 30 : 0 },
      },
      yAxis: {
        type: 'value',
        min: 0,
        name: t('admin.content.games.statistics.charts.solve_time_unit'),
      },
      series: [
        {
          name: t('admin.content.games.statistics.charts.solve_time'),
          type: 'bar',
          data: dynamicStats.map((stat) => resolveSolveMetric(stat.solveTimeMinutes, 'average') ?? 0),
          emphasis: { focus: 'series' },
        },
      ],
    }
  }, [dynamicLabels, dynamicStats, t])

  const solveTimeMedianOption = useMemo<EChartsOption>(() => {
    if (dynamicStats.length === 0)
      return {
        xAxis: { type: 'category', data: [] },
        yAxis: { type: 'value', min: 0 },
        series: [],
      }

    return {
      tooltip: {
        trigger: 'axis',
        valueFormatter: (value) => `${Number(value).toFixed(2)} min`,
      },
      xAxis: {
        type: 'category',
        data: dynamicLabels,
        axisLabel: { interval: 0, rotate: dynamicLabels.length > 6 ? 30 : 0 },
      },
      yAxis: {
        type: 'value',
        min: 0,
        name: t('admin.content.games.statistics.charts.solve_time_unit'),
      },
      series: [
        {
          name: t('admin.content.games.statistics.charts.solve_time_median'),
          type: 'bar',
          data: dynamicStats.map((stat) => resolveSolveMetric(stat.solveTimeMinutes, 'median') ?? 0),
          emphasis: { focus: 'series' },
        },
      ],
    }
  }, [dynamicLabels, dynamicStats, t])

  const onDownload = () => {
    if (Number.isNaN(numId) || numId <= 0) return

    downloadBlob(
      api.admin.adminGameChallengeStatisticsSheet(numId, { format: 'blob' }),
      `ChallengeStats_${numId}_${Date.now()}.xlsx`,
      setDownloading,
      t,
    )
  }

  const rows = normalized.map((stat) => {
    const attemptsAverage = resolveMetric(stat.attemptsToSolve, 'average')
    const attemptsMedian = resolveMetric(stat.attemptsToSolve, 'median')
    const solveAverage = resolveSolveMetric(stat.solveTimeMinutes, 'average')
    const solveMedian = resolveSolveMetric(stat.solveTimeMinutes, 'median')
    const displayTitle =
      stat.title && stat.title.trim().length > 0
        ? stat.title
        : t('admin.content.games.statistics.labels.untitled')

    return (
      <Table.Tr key={`${stat.challengeId ?? stat.title}`}>
        <Table.Td>{displayTitle}</Table.Td>
        <Table.Td>{stat.category ?? '-'}</Table.Td>
        <Table.Td>{stat.type ?? '-'}</Table.Td>
        <Table.Td>
          {stat.solvedTeamCount ?? 0}/{stat.activatedTeamCount ?? 0}/{stat.totalTeamCount ?? 0}
        </Table.Td>
        <Table.Td>{formatPercentage(stat.completionRate)}</Table.Td>
        <Table.Td>{formatNumber(attemptsAverage)}</Table.Td>
        <Table.Td>{formatNumber(attemptsMedian)}</Table.Td>
        <Table.Td>{stat.type === ChallengeType.DynamicContainer ? formatNumber(solveAverage) : '—'}</Table.Td>
        <Table.Td>{stat.type === ChallengeType.DynamicContainer ? formatNumber(solveMedian) : '—'}</Table.Td>
      </Table.Tr>
    )
  })

  const tableHeaders = (
    <Table.Tr>
      <Table.Th>{t('admin.content.games.statistics.table.challenge')}</Table.Th>
      <Table.Th>{t('admin.content.games.statistics.table.category')}</Table.Th>
      <Table.Th>{t('admin.content.games.statistics.table.type')}</Table.Th>
      <Table.Th>{t('admin.content.games.statistics.table.teams')}</Table.Th>
      <Table.Th>{t('admin.content.games.statistics.table.completion')}</Table.Th>
      <Table.Th>{t('admin.content.games.statistics.table.attempt_avg')}</Table.Th>
      <Table.Th>{t('admin.content.games.statistics.table.attempt_median')}</Table.Th>
      <Table.Th>{t('admin.content.games.statistics.table.solve_avg')}</Table.Th>
      <Table.Th>{t('admin.content.games.statistics.table.solve_median')}</Table.Th>
    </Table.Tr>
  )

  return (
    <WithGameEditTab
      head={
        <Group justify="flex-end">
          <Button
            leftSection={<Icon path={mdiDownload} size={1} />}
            onClick={onDownload}
            disabled={downloading || normalized.length === 0 || Number.isNaN(numId) || numId <= 0}
            loading={downloading}
          >
            {t('admin.button.games.export_statistics')}
          </Button>
        </Group>
      }
      isLoading={!statistics && !error}
    >
      <Stack gap="lg">
        {error && (
          <Alert color="red" title={t('common.error.title')}>
            {t('common.error.unknown')}
          </Alert>
        )}

        {!statistics && !error && <Skeleton h={340} radius="md" />}

        {statistics && statistics.length === 0 && (
          <Card>
            <Text>{t('admin.content.games.statistics.empty')}</Text>
          </Card>
        )}

        {statistics && statistics.length > 0 && (
          <>
            <SimpleGrid cols={{ base: 1, lg: 2 }} spacing="lg">
              <Card shadow="sm" padding="lg" radius="md">
                <Title order={4} mb="sm">
                  {t('admin.content.games.statistics.charts.completion')}
                </Title>
                <EchartsContainer option={completionOption} style={{ height: 320 }} />
              </Card>
              <Card shadow="sm" padding="lg" radius="md">
                <Title order={4} mb="sm">
                  {t('admin.content.games.statistics.charts.attempts')}
                </Title>
                <EchartsContainer option={attemptsOption} style={{ height: 320 }} />
              </Card>
            </SimpleGrid>

            {dynamicStats.length > 0 && (
              <>
                <Card shadow="sm" padding="lg" radius="md">
                  <Title order={4} mb="sm">
                    {t('admin.content.games.statistics.charts.solve_time')}
                  </Title>
                  <EchartsContainer option={solveTimeAverageOption} style={{ height: 320 }} />
                </Card>
                <Card shadow="sm" padding="lg" radius="md">
                  <Title order={4} mb="sm">
                    {t('admin.content.games.statistics.charts.solve_time_median')}
                  </Title>
                  <EchartsContainer option={solveTimeMedianOption} style={{ height: 320 }} />
                </Card>
              </>
            )}

            <Card shadow="sm" padding="lg" radius="md">
              <Group justify="space-between" mb="sm">
                <Title order={4}>{t('admin.content.games.statistics.table.title')}</Title>
                {game && (
                  <Text size="sm" c="dimmed">
                    {t('admin.content.games.statistics.table.summary', {
                      team: statistics[0]?.totalTeamCount ?? 0,
                      title: game.title,
                    })}
                  </Text>
                )}
              </Group>
              <Table.ScrollContainer minWidth={700}>
                <Table striped highlightOnHover withTableBorder withColumnBorders>
                  <Table.Thead>{tableHeaders}</Table.Thead>
                  <Table.Tbody>{rows}</Table.Tbody>
                </Table>
              </Table.ScrollContainer>
            </Card>
          </>
        )}
      </Stack>
    </WithGameEditTab>
  )
}

export default GameStatisticsPage
