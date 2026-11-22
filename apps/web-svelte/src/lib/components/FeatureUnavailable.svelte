<script lang="ts">
  /**
   * Shown instead of a page whose product area is switched off for this install, for example an
   * old bookmark to Movies when the simple library profile is on. Offers a way back home.
   */
  import { EyeOff, House } from "@lucide/svelte";
  import { buttonVariants } from "@prismedia/ui-svelte";
  import StatePlaceholder from "./StatePlaceholder.svelte";
</script>

<div class="mx-auto flex w-full max-w-xl flex-col py-10">
  <StatePlaceholder
    icon={EyeOff}
    title="This page is not available"
    description="This part of Prismedia is turned off on this server. Your files and data are untouched."
  >
    <a href="/" class={buttonVariants({ variant: "secondary", size: "sm" })}>
      <House class="h-3.5 w-3.5" />Go home
    </a>
  </StatePlaceholder>
</div>
