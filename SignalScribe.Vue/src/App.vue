<script setup lang="ts">
import { ref } from "vue";

const drawer = ref(true);
</script>

<template>
  <v-app>
    <v-app-bar density="compact" title="SignalScribe">
      <template #prepend>
        <v-app-bar-nav-icon @click="drawer = !drawer" />
      </template>
    </v-app-bar>

    <v-navigation-drawer v-model="drawer">
      <v-list nav density="compact">
        <v-list-item to="/" prepend-icon="mdi-view-dashboard" title="Dashboard" exact />
        <v-list-item to="/channels" prepend-icon="mdi-radio-tower" title="Channels" />
        <v-list-item to="/discards" prepend-icon="mdi-delete-clock" title="Discarded" />
        <v-list-item to="/settings" prepend-icon="mdi-cog" title="Settings" />
      </v-list>
    </v-navigation-drawer>

    <v-main>
      <router-view />
    </v-main>
  </v-app>
</template>
