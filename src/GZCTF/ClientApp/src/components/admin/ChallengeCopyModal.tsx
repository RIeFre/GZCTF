import {
  Box,
  Button,
  Checkbox,
  Group,
  Loader,
  Modal,
  ModalProps,
  MultiSelect,
  Select,
  Stack,
  Text,
} from '@mantine/core'
import { showNotification } from '@mantine/notifications'
import { mdiCheck } from '@mdi/js'
import { Icon } from '@mdi/react'
import { FC, useCallback, useEffect, useMemo, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { showErrorMsg } from '@Utils/Shared'
import api, { ArrayResponseOfGameInfoModel, ChallengeInfoModel, GameInfoModel } from '@Api'

interface ChallengeCopyModalProps extends ModalProps {
  currentGameId: number
  onCopied: (challenges: ChallengeInfoModel[]) => void
}

export const ChallengeCopyModal: FC<ChallengeCopyModalProps> = ({
  currentGameId,
  onCopied,
  opened,
  onClose,
  ...modalProps
}) => {
  const { t } = useTranslation()

  const [games, setGames] = useState<GameInfoModel[]>([])
  const [loadingGames, setLoadingGames] = useState(false)
  const [sourceGameId, setSourceGameId] = useState<string | null>(null)
  const [challenges, setChallenges] = useState<ChallengeInfoModel[]>([])
  const [loadingChallenges, setLoadingChallenges] = useState(false)
  const [selectedChallenges, setSelectedChallenges] = useState<string[]>([])
  const [copyAll, setCopyAll] = useState(true)
  const [submitting, setSubmitting] = useState(false)

  const resetState = useCallback(() => {
    setSourceGameId(null)
    setChallenges([])
    setSelectedChallenges([])
    setCopyAll(true)
  }, [])

  const loadGames = useCallback(async () => {
    setLoadingGames(true)
    try {
      const res = await api.edit.editGetGames({ count: 100, skip: 0 })
      const rawList = (res as ArrayResponseOfGameInfoModel | undefined)?.data
      const nestedList = Array.isArray((res as any)?.data?.data) ? (res as any).data.data : []
      const list = Array.isArray(rawList) ? rawList : nestedList
      const filtered = list.filter((game) => game.id !== currentGameId)
      setGames(filtered)
    } catch (err) {
      showErrorMsg(err, t)
    } finally {
      setLoadingGames(false)
    }
  }, [currentGameId, t])

  const loadChallenges = useCallback(
    async (gameId: number) => {
      setLoadingChallenges(true)
      try {
        const res = await api.edit.editGetGameChallenges(gameId)
        const list = Array.isArray(res)
          ? res
          : Array.isArray((res as any)?.data)
            ? (res as any).data
            : []
        setChallenges(list)
      } catch (err) {
        setChallenges([])
        showErrorMsg(err, t)
      } finally {
        setLoadingChallenges(false)
      }
    },
    [t]
  )

  useEffect(() => {
    if (opened) {
      resetState()
      void loadGames()
    }
  }, [opened, loadGames, resetState])

  useEffect(() => {
    if (!opened) return
    if (!sourceGameId) {
      setChallenges([])
      setSelectedChallenges([])
      return
    }

    void loadChallenges(Number(sourceGameId))
  }, [sourceGameId, loadChallenges, opened])

  const challengeOptions = useMemo(
    () =>
      challenges.map((challenge) => ({
        value: `${challenge.id}`,
        label: challenge.title,
      })),
    [challenges]
  )

  const onSubmit = async () => {
    if (!sourceGameId) {
      showErrorMsg({ message: t('admin.content.games.challenges.copy.source_required') }, t)
      return
    }

    if (!copyAll && selectedChallenges.length === 0) {
      showNotification({
        color: 'red',
        message: t('admin.content.games.challenges.copy.select_warning'),
      })
      return
    }

    setSubmitting(true)

    try {
      const payload = {
        sourceGameId: Number(sourceGameId),
        challengeIds: copyAll ? undefined : selectedChallenges.map((id) => Number(id)),
      }

      const res = await api.edit.editCopyGameChallenges(currentGameId, payload)
      showNotification({
        color: 'teal',
        message: t('admin.notification.games.challenges.copied'),
        icon: <Icon path={mdiCheck} size={1} />,
      })
      onCopied(res ?? [])
      onClose?.()
    } catch (err) {
      showErrorMsg(err, t)
    } finally {
      setSubmitting(false)
    }
  }

  return (
    <Modal opened={opened} onClose={onClose} title={t('admin.content.games.challenges.copy.title')} {...modalProps}>
      <Stack>
        <Select
          label={t('admin.content.games.challenges.copy.source_label')}
          placeholder={loadingGames ? t('common.content.loading') : t('admin.content.games.challenges.copy.source_placeholder')}
          data={games.map((game) => ({ value: `${game.id}`, label: game.title }))}
          value={sourceGameId}
          onChange={(value) => {
            setSourceGameId(value)
            setCopyAll(true)
            setSelectedChallenges([])
          }}
          searchable
          nothingFoundMessage={loadingGames ? t('common.content.loading') : t('admin.content.games.challenges.copy.no_games')}
          rightSection={loadingGames ? <Loader size="xs" /> : undefined}
        />
        <Checkbox
          label={t('admin.content.games.challenges.copy.copy_all')}
          checked={copyAll}
          onChange={(event) => {
            const checked = event.currentTarget.checked
            setCopyAll(checked)
            if (checked) setSelectedChallenges([])
          }}
          disabled={!sourceGameId || loadingChallenges}
        />
        <Box>
          <Text size="sm" c="dimmed" mb="xs">
            {t('admin.content.games.challenges.copy.challenge_label')}
          </Text>
          <MultiSelect
            disabled={!sourceGameId || copyAll}
            data={challengeOptions}
            value={selectedChallenges}
            onChange={setSelectedChallenges}
            searchable
            placeholder={
              loadingChallenges
                ? t('common.content.loading')
                : t('admin.content.games.challenges.copy.challenge_placeholder')
            }
            nothingFound={
              loadingChallenges
                ? t('common.content.loading')
                : t('admin.content.games.challenges.copy.no_challenges')
            }
            rightSection={loadingChallenges ? <Loader size="xs" /> : undefined}
          />
        </Box>
        <Group justify="flex-end">
          <Button variant="default" onClick={onClose} disabled={submitting}>
            {t('common.button.cancel')}
          </Button>
          <Button onClick={onSubmit} loading={submitting} disabled={!sourceGameId}>
            {t('admin.content.games.challenges.copy.confirm')}
          </Button>
        </Group>
      </Stack>
    </Modal>
  )
}
