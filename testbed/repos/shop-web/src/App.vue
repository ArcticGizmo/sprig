<script setup>
import { onMounted, ref } from 'vue'
import { settings, probeAll } from './config.js'

const port = window.location.port
const catalogUrl = import.meta.env.VITE_CATALOG_URL
const ordersUrl = import.meta.env.VITE_ORDERS_URL

const tab = ref('shop')
const products = ref([])
const error = ref('')
const message = ref('')
const email = ref('buyer@example.com')

async function loadProducts() {
  error.value = ''
  try {
    products.value = await (await fetch(`${catalogUrl}/products`)).json()
  } catch {
    error.value = `Couldn't reach the catalog at ${catalogUrl}.`
  }
}

async function order(product) {
  message.value = ''
  try {
    const res = await fetch(`${ordersUrl}/orders`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ productId: product.id, quantity: 1, email: email.value }),
    })
    const body = await res.json()
    message.value = res.ok
      ? `Order ${body.order.id} placed.${body.mailSent ? ' Confirmation emailed.' : ` Email not sent: ${body.mailError}`}`
      : `Order failed: ${body.detail ?? body.error ?? res.status}`
  } catch {
    message.value = `Couldn't reach orders at ${ordersUrl}.`
  }
}

onMounted(() => { loadProducts(); probeAll() })
</script>

<template>
  <header>
    <strong>Shop</strong>
    <nav>
      <button :class="{ on: tab === 'shop' }" @click="tab = 'shop'">Shop</button>
      <button :class="{ on: tab === 'config' }" @click="tab = 'config'; probeAll()">Config</button>
    </nav>
  </header>

  <main v-if="tab === 'shop'">
    <p v-if="error" class="bad">{{ error }}</p>
    <label>Email <input v-model="email" /></label>
    <ul class="products">
      <li v-for="p in products" :key="p.id">
        <span>{{ p.name }}</span>
        <span class="muted">£{{ p.price.toFixed(2) }} · {{ p.stock }} in stock</span>
        <button @click="order(p)">Order</button>
      </li>
    </ul>
    <p v-if="message">{{ message }}</p>
  </main>

  <main v-else>
    <h2>shop-web config</h2>
    <table>
      <tr><th>Setting</th><th>Value</th><th>Came from</th><th>Service</th></tr>
      <tr v-for="s in settings" :key="s.key">
        <td><code>{{ s.key }}</code></td>
        <td><code>{{ s.value ?? '(not set)' }}</code></td>
        <td>{{ s.source }}</td>
        <td :class="s.status.value.endsWith('ok') ? 'ok' : s.status.value === 'checking' ? 'muted' : 'bad'">{{ s.status.value }}</td>
      </tr>
      <tr>
        <td><code>port</code></td>
        <td><code>{{ port }}</code></td>
        <td>PORT</td>
        <td class="ok">serving this page</td>
      </tr>
    </table>
  </main>
</template>
