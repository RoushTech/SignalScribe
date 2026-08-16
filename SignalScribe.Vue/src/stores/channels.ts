import { defineStore } from "pinia";
import { ChannelsApi, type ChannelDto } from "@/api/ChannelsApi";

// Pinia justified per convention: the channel list is shared across dashboard, list, and detail views.
export const useChannelsStore = defineStore("channels", {
  state: () => ({
    channels: [] as ChannelDto[],
    loaded: false,
  }),
  actions: {
    async refresh() {
      this.channels = await ChannelsApi.getAll();
      this.loaded = true;
    },
    byId(id: number): ChannelDto | undefined {
      return this.channels.find((c) => c.id === id);
    },
  },
});
