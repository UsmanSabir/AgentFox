<script lang="ts">
  import { page } from '$app/stores';
</script>

<div class="error-page">
  <div class="error-content">
    <div class="error-code">{$page.status}</div>
    <h1 class="error-title">{$page.error?.message ?? 'Something went wrong'}</h1>
    <a href="/" class="btn btn-ghost">Back to Dashboard</a>
  </div>
</div>

<style>
  .error-page {
    display: flex;
    align-items: center;
    justify-content: center;
    min-height: 100vh;
    padding: 2rem;
  }

  .error-content {
    text-align: center;
    max-width: 400px;
  }

  .error-code {
    font-size: 5rem;
    font-weight: 900;
    color: var(--surface-3);
    line-height: 1;
    margin-bottom: 0.5rem;
    background: linear-gradient(135deg, var(--primary), var(--accent));
    -webkit-background-clip: text;
    -webkit-text-fill-color: transparent;
  }

  .error-title {
    font-size: 1.125rem;
    font-weight: 600;
    color: var(--text-2);
    margin: 0 0 1.5rem;
  }
</style>
