import { createRouter, createWebHistory } from "vue-router";
import DashboardView from "@/views/DashboardView.vue";
import ChannelsView from "@/views/ChannelsView.vue";
import ChannelDetailView from "@/views/ChannelDetailView.vue";
import SettingsView from "@/views/SettingsView.vue";
import DiscardsView from "@/views/DiscardsView.vue";

export default createRouter({
  history: createWebHistory(),
  routes: [
    { path: "/", name: "dashboard", component: DashboardView },
    { path: "/channels", name: "channels", component: ChannelsView },
    { path: "/channels/:id", name: "channel-detail", component: ChannelDetailView, props: true },
    { path: "/discards", name: "discards", component: DiscardsView },
    { path: "/settings", name: "settings", component: SettingsView },
  ],
});
