<script setup>
import { onMounted, ref } from 'vue'
import {
  IonApp, IonHeader, IonToolbar, IonTitle, IonContent, IonList, IonItem, IonLabel, IonNote,
  IonSegment, IonSegmentButton, IonText,
} from '@ionic/vue'

/* global __ENV_SOURCES__ */
// Templates only see component state, not globals, so the sources are copied into a local first.
const catalogSource = __ENV_SOURCES__.VITE_CATALOG_URL ?? 'not set'
const catalogUrl = import.meta.env.VITE_CATALOG_URL
const tab = ref('catalog')
const products = ref([])
const error = ref('')
const catalogStatus = ref('checking')
const port = window.location.port

async function load() {
  error.value = ''
  try {
    products.value = await (await fetch(`${catalogUrl}/products`)).json()
  } catch {
    error.value = `Couldn't reach the catalog at ${catalogUrl}.`
  }
  try {
    const body = await (await fetch(`${catalogUrl}/health`)).json()
    catalogStatus.value = `${body.app}: ${body.status}`
  } catch {
    catalogStatus.value = 'nothing answering'
  }
}

onMounted(load)
</script>

<template>
  <ion-app>
    <ion-header>
      <ion-toolbar>
        <ion-title>Shop</ion-title>
      </ion-toolbar>
      <ion-toolbar>
        <ion-segment :value="tab" @ionChange="tab = $event.detail.value">
          <ion-segment-button value="catalog"><ion-label>Catalog</ion-label></ion-segment-button>
          <ion-segment-button value="config"><ion-label>Config</ion-label></ion-segment-button>
        </ion-segment>
      </ion-toolbar>
    </ion-header>

    <ion-content class="ion-padding">
      <template v-if="tab === 'catalog'">
        <ion-text v-if="error" color="danger"><p>{{ error }}</p></ion-text>
        <ion-list>
          <ion-item v-for="p in products" :key="p.id">
            <ion-label>{{ p.name }}</ion-label>
            <ion-note slot="end">£{{ p.price.toFixed(2) }}</ion-note>
          </ion-item>
        </ion-list>
      </template>

      <ion-list v-else>
        <ion-item>
          <ion-label>
            <h3>VITE_CATALOG_URL</h3>
            <p>{{ catalogUrl ?? '(not set)' }}</p>
            <p>from {{ catalogSource }}</p>
          </ion-label>
          <ion-note slot="end" :color="catalogStatus.endsWith('ok') ? 'success' : 'danger'">{{ catalogStatus }}</ion-note>
        </ion-item>
        <ion-item>
          <ion-label>
            <h3>port</h3>
            <p>{{ port }} (from PORT)</p>
          </ion-label>
        </ion-item>
      </ion-list>
    </ion-content>
  </ion-app>
</template>
