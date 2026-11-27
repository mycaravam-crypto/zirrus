import { createTheme, type MantineColorsTuple } from "@mantine/core";

/** Brand blue; shade 7 is the primary in light mode (4.5:1 with white text, WCAG AA). */
const brand: MantineColorsTuple = [
  "#eef3ff",
  "#dce4f5",
  "#b9c7e2",
  "#94a8d0",
  "#748dc1",
  "#5f7cb8",
  "#5474b4",
  "#44639f",
  "#39588f",
  "#2d4b81",
];

/**
 * Mantine theme for the explorer. System fonts only (no external resources), AA-contrast
 * primary shades in both color schemes, and automatic text contrast on filled colors.
 */
export const theme = createTheme({
  primaryColor: "brand",
  primaryShade: { light: 7, dark: 8 },
  colors: { brand },
  autoContrast: true,
  defaultRadius: "md",
  cursorType: "pointer",
  fontFamily: 'system-ui, -apple-system, "Segoe UI", Roboto, "Helvetica Neue", Arial, sans-serif',
  fontFamilyMonospace: 'ui-monospace, SFMono-Regular, "SF Mono", Menlo, Consolas, monospace',
  headings: { fontWeight: "650" },
  components: {
    Button: { defaultProps: { fw: 550 } },
    Paper: { defaultProps: { withBorder: true } },
  },
});
