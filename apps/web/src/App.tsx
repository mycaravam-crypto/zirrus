import {
  ActionIcon,
  Alert,
  AppShell,
  Avatar,
  Burger,
  Button,
  Center,
  Group,
  Loader,
  Paper,
  Stack,
  Text,
  ThemeIcon,
  Title,
  useComputedColorScheme,
  useMantineColorScheme,
} from "@mantine/core";
import { useDisclosure } from "@mantine/hooks";
import { IconAlertCircle, IconLogin2, IconLogout, IconMoon, IconPhoto, IconSun } from "@tabler/icons-react";
import { useQuery } from "@tanstack/react-query";
import type { ZirrusClient } from "@zirrus/api-client";
import { ApiError } from "./api";
import { Explorer } from "./explorer/Explorer";
import { t } from "./i18n";

/** Query key of the signed-in user; a 401 anywhere resets it to null (signed out). */
export const meKey = ["me"] as const;

/**
 * Signed-in shell: header with the user and sign-out, catalog navigation and the explorer.
 * Anonymous visitors get a sign-in card.
 * @param client API client.
 */
export function App({ client }: { client: ZirrusClient }) {
  // On phones the catalog navigation starts open, so the first tap chooses a catalog.
  const [navOpened, nav] = useDisclosure(true);
  const me = useQuery({
    queryKey: meKey,
    queryFn: async () => {
      const { data, response } = await client.GET("/api/v1/me");
      if (response.status === 401) return null;
      if (data === undefined) throw new ApiError(response.status, "Could not load the current user");
      return data;
    },
  });
  const user = me.isSuccess ? me.data : null;
  const signedIn = user !== null;

  return (
    <AppShell
      header={{ height: 60 }}
      navbar={signedIn ? { width: 280, breakpoint: "sm", collapsed: { mobile: !navOpened } } : undefined}
      padding="lg"
    >
      <AppShell.Header>
        <Group h="100%" px="md" justify="space-between" wrap="nowrap">
          <Group gap="sm" wrap="nowrap">
            {signedIn && <Burger opened={navOpened} onClick={nav.toggle} hiddenFrom="sm" size="sm" aria-label={t("app.toggleNav")} />}
            <ThemeIcon size={34} radius="md" variant="filled" aria-hidden="true">
              <IconPhoto size={20} stroke={1.8} />
            </ThemeIcon>
            <Title order={1} size="h3" className="brand-title">
              {t("app.title")}
            </Title>
          </Group>
          <Group gap="sm" wrap="nowrap">
            <ColorSchemeToggle />
            {user !== null && (
              <>
                <Avatar color="brand" radius="xl" size={34} visibleFrom="xs" aria-hidden="true">
                  {initials(user.name || user.subject)}
                </Avatar>
                <Text size="sm" visibleFrom="md">
                  {t("app.signedInAs", { name: user.name || user.subject })}
                  {user.isOrganizationAdmin && ` (${t("app.orgAdmin")})`}
                </Text>
                <SignOut client={client} />
              </>
            )}
          </Group>
        </Group>
      </AppShell.Header>

      {me.isPending && (
        <AppShell.Main>
          <Center mih="50vh">
            <Loader aria-hidden="true" />
            <Text role="status" className="sr-only">
              {t("app.loading")}
            </Text>
          </Center>
        </AppShell.Main>
      )}
      {me.isError && (
        <AppShell.Main>
          <Alert color="red.9" variant="light" icon={<IconAlertCircle />} maw={560} mx="auto" mt="xl" title={t("app.unreachable")}>
            {t("app.unreachableHint")}
          </Alert>
        </AppShell.Main>
      )}
      {me.isSuccess && me.data === null && (
        <AppShell.Main>
          <Center mih="70vh">
            <Paper p="xl" radius="lg" shadow="sm" maw={420} w="100%">
              <Stack align="center" gap="md" ta="center">
                <ThemeIcon size={56} radius="xl" variant="light" aria-hidden="true">
                  <IconPhoto size={30} stroke={1.6} />
                </ThemeIcon>
                <Title order={2} size="h3">
                  {t("app.welcome")}
                </Title>
                <Text c="dimmed">{t("app.tagline")}</Text>
                <Button component="a" href="/login?returnUrl=/" size="md" fullWidth leftSection={<IconLogin2 size={18} />}>
                  {t("app.signIn")}
                </Button>
              </Stack>
            </Paper>
          </Center>
        </AppShell.Main>
      )}
      {signedIn && <Explorer client={client} onNavigate={nav.close} />}
    </AppShell>
  );
}

/**
 * Switches between light and dark mode (the default follows the operating system).
 */
function ColorSchemeToggle() {
  const { setColorScheme } = useMantineColorScheme();
  const computed = useComputedColorScheme("light", { getInitialValueInEffect: true });
  return (
    <ActionIcon
      variant="default"
      size={34}
      aria-label={t("app.toggleTheme")}
      onClick={() => setColorScheme(computed === "light" ? "dark" : "light")}
    >
      {computed === "light" ? <IconMoon size={18} stroke={1.6} /> : <IconSun size={18} stroke={1.6} />}
    </ActionIcon>
  );
}

/**
 * Up to two initials for the avatar.
 * @param name Display name or subject.
 * @returns Uppercase initials.
 */
function initials(name: string) {
  return name
    .split(/\s+/)
    .filter(Boolean)
    .slice(0, 2)
    .map((part) => part[0]?.toUpperCase() ?? "")
    .join("");
}

/**
 * Sign-out is a form POST with the CSRF token, so the API can end the identity-provider session too.
 * @param client API client.
 */
function SignOut({ client }: { client: ZirrusClient }) {
  const token = useQuery({
    queryKey: ["csrf"],
    queryFn: async () => {
      const { data, response } = await client.GET("/api/v1/antiforgery");
      if (data === undefined) throw new ApiError(response.status, "Could not load the CSRF token");
      return data;
    },
  });

  if (!token.isSuccess) return null;
  return (
    <form method="post" action="/logout" className="inline-form">
      <input type="hidden" name={token.data.formFieldName} value={token.data.token} />
      <Button type="submit" variant="default" size="sm" leftSection={<IconLogout size={16} />}>
        {t("app.signOut")}
      </Button>
    </form>
  );
}
