<script lang="ts">
  import { onMount, onDestroy, tick } from 'svelte';
  import { streamChat, api, type SessionInfo, type SpecialistAgentInfo } from '$lib/api';
  import { renderMarkdown } from '$lib/markdown';
  import {
    chatMessages, addUserMessage, addAssistantMessage, addBackgroundResultMessage,
    appendToken, finalizeMessage, activeConversationId, activeAgentId, agentReady, resetChat
  } from '$lib/stores';
  import {
    Send, RotateCcw, StopCircle, Bot, User, Copy, Check, Zap, History, Plus, X
  } from 'lucide-svelte';

  let inputEl: HTMLTextAreaElement;
  let scrollEl: HTMLDivElement;
  let message = '';
  let isStreaming = false;
  let abortCtrl: AbortController | null = null;
  let copiedId: string | null = null;
  let pollTimer: ReturnType<typeof setInterval> | null = null;
  let sessions: SessionInfo[] = [];
  let specialists: SpecialistAgentInfo[] = [];
  let showSessions = false;
  let loadingSession = false;

  $: messages     = $chatMessages;
  $: convId       = $activeConversationId;
  $: agentIsReady = $agentReady;
  $: selectedAgentId = $activeAgentId;
  $: selectedAgentName = selectedAgentId === 'main'
    ? 'AgentFox'
    : specialists.find(agent => agent.id === selectedAgentId)?.name ?? selectedAgentId;

  // Start / restart polling whenever the conversation ID changes.
  // Polling is intentionally keyed to the conversation, not to isStreaming,
  // so background results arrive even while the user is mid-conversation.
  $: {
    const cid = $activeConversationId;
    if (pollTimer) { clearInterval(pollTimer); pollTimer = null; }
    if (cid) pollTimer = setInterval(() => pollPending(cid), 3000);
  }

  onMount(async () => {
    inputEl?.focus();
    await Promise.all([loadSessions(), loadSpecialists()]);
  });

  onDestroy(() => {
    if (pollTimer) clearInterval(pollTimer);
  });

  async function pollPending(cid: string) {
    try {
      const data = await api.pendingNotifications(cid);
      if (data.count > 0) {
        for (const n of data.notifications) {
          addBackgroundResultMessage(n.message);
        }
        await scrollToBottom();
      }
    } catch {
      // silently ignore poll errors (server may be restarting)
    }
  }

  // Whether the view should follow new content. Stays true while the user is
  // at (or near) the bottom; flips off if they scroll up to read history.
  let autoStick = true;

  function onScroll() {
    if (!scrollEl) return;
    const distanceFromBottom =
      scrollEl.scrollHeight - scrollEl.scrollTop - scrollEl.clientHeight;
    autoStick = distanceFromBottom < 80;
  }

  async function scrollToBottom(force = false) {
    if (force) autoStick = true;
    if (!autoStick) return;
    // Wait for Svelte to flush the DOM, then for the browser to lay it out,
    // so scrollHeight reflects the newly rendered content before we jump.
    await tick();
    requestAnimationFrame(() => {
      if (autoStick && scrollEl) scrollEl.scrollTop = scrollEl.scrollHeight;
    });
  }

  async function send() {
    const text = message.trim();
    if (!text || isStreaming) return;

    message = '';
    addUserMessage(text);
    await scrollToBottom(true);

    const assistantId = addAssistantMessage('', true);
    isStreaming = true;
    abortCtrl   = new AbortController();

    try {
      if (selectedAgentId === 'main') {
        const gen = streamChat(text, convId, abortCtrl.signal);
        for await (const event of gen) {
          if (event.type === 'token') {
            appendToken(assistantId, event.token);
            await scrollToBottom();
          } else if (event.type === 'done') {
            if (event.conversationId) activeConversationId.set(event.conversationId);
            finalizeMessage(assistantId);
            break;
          } else if (event.type === 'error') {
            finalizeMessage(assistantId, event.error);
            break;
          }
        }
      } else {
        const response = await api.specialistChat(selectedAgentId, text, convId);
        if (response.conversationId) activeConversationId.set(response.conversationId);
        if (response.success) {
          appendToken(assistantId, response.response);
          finalizeMessage(assistantId);
        } else {
          finalizeMessage(assistantId, response.error ?? 'Specialist request failed');
        }
      }
    } catch (err: unknown) {
      const msg = err instanceof Error ? err.message : String(err);
      if (msg !== 'AbortError' && !msg.includes('abort')) {
        finalizeMessage(assistantId, msg);
      } else {
        finalizeMessage(assistantId);
      }
    } finally {
      isStreaming = false;
      abortCtrl   = null;
      await scrollToBottom();
      await tick();
      inputEl?.focus();
      await loadSessions();
    }
  }

  function stop() {
    abortCtrl?.abort();
  }

  function clearChat() {
    resetChat(selectedAgentId);
  }

  async function loadSessions() {
    try { sessions = await api.sessions(); } catch { sessions = []; }
  }

  async function loadSpecialists() {
    try { specialists = await api.specialistAgents(); } catch { specialists = []; }
  }

  async function openSession(session: SessionInfo) {
    if (isStreaming || loadingSession) return;
    loadingSession = true;
    try {
      if (session.status.toLowerCase() === 'archived') await api.resumeSession(session.id);
      const history = await api.sessionMessages(session.id);
      activeAgentId.set(specialists.some(agent => agent.id === history.agentId) ? history.agentId : 'main');
      activeConversationId.set(history.conversationId);
      chatMessages.set(history.messages.map(item => ({
        id: crypto.randomUUID(),
        role: item.role,
        content: item.content,
        timestamp: new Date(session.lastActive)
      })));
      showSessions = false;
      await loadSessions();
      await scrollToBottom(true);
    } finally {
      loadingSession = false;
    }
  }

  function changeAgent(event: Event) {
    const id = (event.currentTarget as HTMLSelectElement).value;
    resetChat(id);
  }

  function handleKeyDown(e: KeyboardEvent) {
    if (e.key === 'Enter' && !e.shiftKey) {
      e.preventDefault();
      send();
    }
  }

  async function copyContent(id: string, content: string) {
    await navigator.clipboard.writeText(content);
    copiedId = id;
    setTimeout(() => { copiedId = null; }, 1500);
  }

  function autoResize(node: HTMLTextAreaElement) {
    function resize() {
      node.style.height = 'auto';
      node.style.height = Math.min(node.scrollHeight, 160) + 'px';
    }
    node.addEventListener('input', resize);
    return { destroy() { node.removeEventListener('input', resize); } };
  }
</script>

<div class="chat-shell">
  <div class="chat-toolbar">
    <button class="toolbar-btn" on:click={() => showSessions = !showSessions} title="Browse sessions">
      <History size={14} /> Sessions
    </button>
    <label class="agent-picker">
      <span>Agent</span>
      <select value={selectedAgentId} on:change={changeAgent} disabled={isStreaming}>
        <option value="main">AgentFox</option>
        {#each specialists as specialist}
          <option value={specialist.id}>{specialist.name}</option>
        {/each}
      </select>
    </label>
    {#if convId}
      <span class="conv-label">Session:</span>
      <code class="conv-id">{convId}</code>
    {/if}
    <button class="toolbar-btn new-chat-btn" on:click={clearChat} disabled={isStreaming} title="Start a new chat">
      <Plus size={14} /> New chat
    </button>
  </div>

  <div class="chat-main">
    {#if showSessions}
      <aside class="session-panel fade-in">
        <div class="session-panel-head">
          <div><strong>Sessions</strong><span>{sessions.length}</span></div>
          <button class="icon-btn" on:click={() => showSessions = false} title="Close"><X size={15} /></button>
        </div>
        <div class="session-list">
          {#if sessions.length === 0}
            <p class="session-empty">No saved sessions yet.</p>
          {:else}
            {#each sessions as session (session.id)}
              <button
                class="session-item"
                class:active={session.id === convId}
                on:click={() => openSession(session)}
                disabled={loadingSession || isStreaming}
              >
                <span class="session-item-title">{session.id}</span>
                <span class="session-item-meta">{session.origin} · {session.status}</span>
                <span class="session-item-time">{new Date(session.lastActive).toLocaleString()}</span>
              </button>
            {/each}
          {/if}
        </div>
      </aside>
    {/if}

    <!-- Messages -->
    <div class="messages-wrap" bind:this={scrollEl} on:scroll={onScroll}>
    {#if messages.length === 0}
      <div class="intro fade-in">
        <div class="intro-icon">
          <Zap size={28} />
        </div>
        <h2 class="intro-title">AgentFox Chat</h2>
        <p class="intro-sub">Real-time streaming · Tool use · Memory · Sub-agents</p>
        <div class="suggestions">
          {#each [
            'What tools do you have available?',
            'Search the web for latest AI news',
            'Help me write a Python script',
            'What do you remember from our past conversations?'
          ] as s}
            <button
              class="suggestion"
              on:click={() => { message = s; send(); }}
              disabled={isStreaming}
            >{s}</button>
          {/each}
        </div>
      </div>
    {:else}
      <div class="messages">
        {#each messages as msg (msg.id)}
          <div class="message {msg.role} fade-in">
            <div class="message-avatar">
              {#if msg.role === 'user'}
                <User size={14} />
              {:else}
                <Bot size={14} />
              {/if}
            </div>
            <div class="message-body">
              <div class="message-meta">
                <span class="message-role">{msg.role === 'user' ? 'You' : selectedAgentName}</span>
                {#if msg.isBackgroundResult}
                  <span class="bg-badge">background result</span>
                {/if}
                <span class="message-time">{msg.timestamp.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })}</span>
              </div>

              {#if msg.error}
                <div class="message-error">{msg.error}</div>
              {:else if msg.role === 'user'}
                <div class="message-content user-text">{msg.content}</div>
              {:else}
                <div
                  class="message-content markdown"
                  class:stream-cursor={msg.streaming && msg.content.length > 0}
                >{#if msg.content.length > 0}{@html renderMarkdown(msg.content)}{:else if msg.streaming}<span class="typing-dots"><span></span><span></span><span></span></span>{/if}</div>
              {/if}

              {#if !msg.streaming && msg.role === 'assistant' && !msg.error}
                <button
                  class="copy-btn"
                  on:click={() => copyContent(msg.id, msg.content)}
                  title="Copy response"
                >
                  {#if copiedId === msg.id}
                    <Check size={12} />
                    <span>Copied</span>
                  {:else}
                    <Copy size={12} />
                    <span>Copy</span>
                  {/if}
                </button>
              {/if}
            </div>
          </div>
        {/each}
      </div>
    {/if}
    </div>
  </div>

  <!-- Input bar -->
  <div class="input-bar">
    <div class="input-wrap">
      <textarea
        bind:this={inputEl}
        bind:value={message}
        use:autoResize
        on:keydown={handleKeyDown}
        placeholder={agentIsReady ? 'Message AgentFox… (Enter to send, Shift+Enter for newline)' : 'Waiting for agent…'}
        disabled={!agentIsReady}
        rows="1"
        class="chat-input"
      ></textarea>

      <div class="input-actions">
        {#if messages.length > 0}
          <button class="icon-btn" on:click={clearChat} title="Clear chat" disabled={isStreaming}>
            <RotateCcw size={15} />
          </button>
        {/if}

        {#if isStreaming}
          <button class="stop-btn" on:click={stop} title="Stop generation">
            <StopCircle size={15} />
            <span>Stop</span>
          </button>
        {:else}
          <button
            class="send-btn"
            on:click={send}
            disabled={!message.trim() || !agentIsReady}
            title="Send message"
          >
            <Send size={15} />
          </button>
        {/if}
      </div>
    </div>
    <p class="input-hint">AgentFox can use tools, access memory, and spawn sub-agents.</p>
  </div>
</div>

<style>
  .chat-shell {
    display: flex;
    flex-direction: column;
    position: relative;
    height: calc(100vh - var(--header-h));
    overflow: hidden;
  }

  .chat-toolbar {
    display: flex;
    align-items: center;
    gap: 0.5rem;
    min-height: 42px;
    padding: 0.375rem 1rem;
    background: var(--surface);
    border-bottom: 1px solid var(--border);
    font-size: 0.75rem;
    color: var(--text-3);
  }
  .toolbar-btn {
    display: inline-flex;
    align-items: center;
    gap: 0.35rem;
    padding: 0.3rem 0.55rem;
    border-radius: var(--radius-sm);
    border: 1px solid var(--border-md);
    background: var(--surface-2);
    color: var(--text-2);
    cursor: pointer;
    font-size: 0.75rem;
  }
  .toolbar-btn:hover { color: var(--text); border-color: var(--border-high); }
  .toolbar-btn:disabled { opacity: 0.45; cursor: not-allowed; }
  .new-chat-btn { margin-left: auto; color: var(--primary); }
  .agent-picker { display: flex; align-items: center; gap: 0.4rem; }
  .agent-picker select {
    background: var(--surface-2);
    color: var(--text);
    border: 1px solid var(--border-md);
    border-radius: var(--radius-sm);
    padding: 0.25rem 1.8rem 0.25rem 0.45rem;
    font-size: 0.75rem;
  }
  .conv-label { color: var(--text-3); }
  .conv-id {
    font-family: monospace;
    background: var(--surface-2);
    padding: 0.1em 0.4em;
    border-radius: 4px;
    color: var(--text-2);
    max-width: 38vw;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }
  .chat-main { display: flex; flex: 1; min-height: 0; overflow: hidden; }
  .session-panel {
    width: 290px;
    flex-shrink: 0;
    background: var(--surface);
    border-right: 1px solid var(--border);
    display: flex;
    flex-direction: column;
    min-height: 0;
  }
  .session-panel-head {
    display: flex;
    justify-content: space-between;
    align-items: center;
    padding: 0.8rem;
    border-bottom: 1px solid var(--border);
  }
  .session-panel-head > div { display: flex; align-items: center; gap: 0.45rem; }
  .session-panel-head span {
    font-size: 0.65rem;
    color: var(--text-3);
    background: var(--surface-2);
    border-radius: 99px;
    padding: 0.1rem 0.4rem;
  }
  .session-list { overflow-y: auto; padding: 0.45rem; }
  .session-item {
    width: 100%;
    text-align: left;
    display: flex;
    flex-direction: column;
    gap: 0.2rem;
    border: 1px solid transparent;
    border-radius: 7px;
    background: transparent;
    color: var(--text);
    padding: 0.65rem;
    cursor: pointer;
  }
  .session-item:hover { background: var(--surface-2); border-color: var(--border); }
  .session-item.active { background: var(--primary-dim); border-color: rgba(129,140,248,0.35); }
  .session-item:disabled { opacity: 0.55; cursor: wait; }
  .session-item-title { font-family: monospace; font-size: 0.72rem; word-break: break-all; }
  .session-item-meta, .session-item-time { color: var(--text-3); font-size: 0.65rem; }
  .session-empty { color: var(--text-3); font-size: 0.75rem; padding: 1rem; text-align: center; }
  .icon-btn {
    background: transparent;
    border: none;
    cursor: pointer;
    color: var(--text-3);
    padding: 0.2rem;
    border-radius: 4px;
    display: flex;
    align-items: center;
    transition: color 0.1s;
  }
  .icon-btn:hover { color: var(--text); }
  .icon-btn:disabled { opacity: 0.4; cursor: not-allowed; }

  /* Messages area */
  .messages-wrap {
    flex: 1;
    min-width: 0;
    overflow-y: auto;
    padding: 1.5rem;
  }

  @media (max-width: 760px) {
    .session-panel { position: absolute; z-index: 20; inset: 42px auto 0 0; box-shadow: 12px 0 30px rgba(0,0,0,0.35); }
    .conv-label, .conv-id { display: none; }
    .agent-picker > span { display: none; }
  }

  /* Intro / empty state */
  .intro {
    display: flex;
    flex-direction: column;
    align-items: center;
    justify-content: center;
    min-height: 50vh;
    text-align: center;
    gap: 0.5rem;
  }

  .intro-icon {
    width: 56px;
    height: 56px;
    border-radius: 14px;
    background: linear-gradient(135deg, var(--primary), var(--accent));
    display: flex;
    align-items: center;
    justify-content: center;
    color: #fff;
    margin-bottom: 0.5rem;
    box-shadow: 0 8px 24px rgba(129,140,248,0.3);
  }

  .intro-title {
    font-size: 1.25rem;
    font-weight: 700;
    margin: 0;
  }

  .intro-sub {
    font-size: 0.8125rem;
    color: var(--text-2);
    margin: 0 0 1rem;
  }

  .suggestions {
    display: flex;
    flex-wrap: wrap;
    gap: 0.5rem;
    justify-content: center;
    max-width: 560px;
  }

  .suggestion {
    background: var(--surface-2);
    border: 1px solid var(--border-md);
    border-radius: 99px;
    padding: 0.35rem 0.875rem;
    font-size: 0.8125rem;
    color: var(--text-2);
    cursor: pointer;
    transition: all 0.15s;
  }
  .suggestion:hover {
    background: var(--surface-3);
    color: var(--text);
    border-color: var(--border-high);
  }
  .suggestion:disabled { opacity: 0.5; cursor: not-allowed; }

  /* Messages list */
  .messages {
    display: flex;
    flex-direction: column;
    gap: 1.5rem;
    max-width: 780px;
    margin: 0 auto;
  }

  .message {
    display: flex;
    gap: 0.875rem;
    align-items: flex-start;
  }

  .message.user { flex-direction: row-reverse; }

  .message-avatar {
    width: 32px;
    height: 32px;
    border-radius: 8px;
    display: flex;
    align-items: center;
    justify-content: center;
    flex-shrink: 0;
  }

  .message.user .message-avatar {
    background: var(--primary-dim);
    color: var(--primary);
  }

  .message.assistant .message-avatar {
    background: linear-gradient(135deg, var(--primary), var(--accent));
    color: #fff;
  }

  .message-body {
    flex: 1;
    min-width: 0;
    max-width: 85%;
    display: flex;
    flex-direction: column;
    gap: 0.25rem;
  }

  .message.user .message-body { align-items: flex-end; }

  .message-meta {
    display: flex;
    align-items: center;
    gap: 0.5rem;
  }
  .message.user .message-meta { flex-direction: row-reverse; }

  .message-role {
    font-size: 0.75rem;
    font-weight: 600;
    color: var(--text-2);
  }

  .message-time {
    font-size: 0.6875rem;
    color: var(--text-3);
  }

  .bg-badge {
    font-size: 0.625rem;
    font-weight: 600;
    letter-spacing: 0.03em;
    text-transform: uppercase;
    background: rgba(129,140,248,0.12);
    color: var(--primary);
    border: 1px solid rgba(129,140,248,0.25);
    border-radius: 99px;
    padding: 0.1em 0.5em;
  }

  .message-content {
    background: var(--surface);
    border: 1px solid var(--border);
    border-radius: 12px;
    padding: 0.75rem 1rem;
    font-size: 0.875rem;
    line-height: 1.65;
    color: var(--text);
    word-break: break-word;
    overflow-wrap: anywhere;
  }

  /* User messages are plain text — preserve their line breaks. */
  .message-content.user-text { white-space: pre-wrap; }

  /* Rendered markdown (assistant / background results) */
  .markdown :global(> *:first-child) { margin-top: 0; }
  .markdown :global(> *:last-child) { margin-bottom: 0; }
  .markdown :global(p) { margin: 0 0 0.75em; }
  .markdown :global(h1),
  .markdown :global(h2),
  .markdown :global(h3),
  .markdown :global(h4) {
    margin: 1.1em 0 0.5em;
    line-height: 1.3;
    font-weight: 700;
  }
  .markdown :global(h1) { font-size: 1.25em; }
  .markdown :global(h2) { font-size: 1.15em; }
  .markdown :global(h3) { font-size: 1.05em; }
  .markdown :global(h4) { font-size: 1em; }
  .markdown :global(ul),
  .markdown :global(ol) { margin: 0 0 0.75em; padding-left: 1.4em; }
  .markdown :global(li) { margin: 0.2em 0; }
  .markdown :global(li > p) { margin: 0; }
  .markdown :global(a) { color: var(--primary); text-decoration: underline; }
  .markdown :global(strong) { font-weight: 700; }
  .markdown :global(blockquote) {
    margin: 0 0 0.75em;
    padding: 0.1em 0.9em;
    border-left: 3px solid var(--border-high);
    color: var(--text-2);
  }
  .markdown :global(hr) {
    border: none;
    border-top: 1px solid var(--border);
    margin: 1em 0;
  }
  .markdown :global(code) {
    font-family: var(--font-mono, ui-monospace, SFMono-Regular, Menlo, monospace);
    font-size: 0.85em;
    background: var(--surface-2);
    border: 1px solid var(--border);
    border-radius: 4px;
    padding: 0.1em 0.35em;
  }
  .markdown :global(pre) {
    margin: 0 0 0.75em;
    padding: 0.75rem 0.9rem;
    background: var(--surface-2);
    border: 1px solid var(--border);
    border-radius: 8px;
    overflow-x: auto;
  }
  .markdown :global(pre code) {
    background: none;
    border: none;
    padding: 0;
    font-size: 0.85em;
  }
  /* Tables (GFM) — scroll horizontally on overflow instead of breaking layout */
  .markdown :global(table) {
    display: block;
    width: max-content;
    max-width: 100%;
    overflow-x: auto;
    border-collapse: collapse;
    margin: 0 0 0.75em;
    font-size: 0.82em;
  }
  .markdown :global(th),
  .markdown :global(td) {
    border: 1px solid var(--border);
    padding: 0.4em 0.65em;
    text-align: left;
    vertical-align: top;
  }
  .markdown :global(th) {
    background: var(--surface-2);
    font-weight: 600;
    white-space: nowrap;
  }

  .message.user .message-content {
    background: var(--primary-dim);
    border-color: rgba(129,140,248,0.2);
    border-radius: 12px 4px 12px 12px;
  }

  .message.assistant .message-content {
    border-radius: 4px 12px 12px 12px;
  }

  .message-error {
    background: rgba(248,113,113,0.1);
    border: 1px solid rgba(248,113,113,0.25);
    border-radius: 8px;
    padding: 0.625rem 0.875rem;
    font-size: 0.8125rem;
    color: var(--danger);
  }

  /* Typing animation */
  .typing-dots {
    display: inline-flex;
    gap: 3px;
    align-items: center;
    padding: 2px 0;
  }
  .typing-dots span {
    display: inline-block;
    width: 5px;
    height: 5px;
    border-radius: 50%;
    background: var(--primary);
    animation: typing-bounce 1.2s ease-in-out infinite;
  }
  .typing-dots span:nth-child(2) { animation-delay: 0.2s; }
  .typing-dots span:nth-child(3) { animation-delay: 0.4s; }

  @keyframes typing-bounce {
    0%, 60%, 100% { transform: translateY(0); opacity: 0.6; }
    30%            { transform: translateY(-4px); opacity: 1; }
  }

  /* Copy button */
  .copy-btn {
    display: inline-flex;
    align-items: center;
    gap: 0.25rem;
    background: transparent;
    border: none;
    cursor: pointer;
    color: var(--text-3);
    font-size: 0.6875rem;
    padding: 0.125rem 0;
    transition: color 0.15s;
    margin-top: 0.125rem;
  }
  .copy-btn:hover { color: var(--text-2); }

  /* Input bar */
  .input-bar {
    padding: 1rem 1.5rem 0.75rem;
    border-top: 1px solid var(--border);
    background: var(--surface);
    flex-shrink: 0;
  }

  .input-wrap {
    display: flex;
    align-items: flex-end;
    gap: 0.5rem;
    background: var(--surface-2);
    border: 1px solid var(--border-md);
    border-radius: var(--radius);
    padding: 0.5rem 0.625rem 0.5rem 0.875rem;
    transition: border-color 0.15s;
  }
  .input-wrap:focus-within { border-color: var(--primary); }

  .chat-input {
    flex: 1;
    background: transparent;
    border: none;
    outline: none;
    color: var(--text);
    font-size: 0.875rem;
    resize: none;
    font-family: inherit;
    line-height: 1.5;
    padding: 0.25rem 0;
    min-height: 24px;
  }
  .chat-input::placeholder { color: var(--text-3); }
  .chat-input:disabled { opacity: 0.5; }

  .input-actions {
    display: flex;
    align-items: center;
    gap: 0.375rem;
    flex-shrink: 0;
  }

  .send-btn {
    width: 34px;
    height: 34px;
    border-radius: var(--radius-sm);
    background: var(--primary);
    border: none;
    cursor: pointer;
    color: #0c0d10;
    display: flex;
    align-items: center;
    justify-content: center;
    transition: background 0.15s, opacity 0.15s;
    flex-shrink: 0;
  }
  .send-btn:hover:not(:disabled) { background: #9199f9; }
  .send-btn:disabled { opacity: 0.4; cursor: not-allowed; }

  .stop-btn {
    display: flex;
    align-items: center;
    gap: 0.375rem;
    padding: 0.375rem 0.75rem;
    border-radius: var(--radius-sm);
    background: rgba(248,113,113,0.12);
    border: 1px solid rgba(248,113,113,0.25);
    color: var(--danger);
    font-size: 0.75rem;
    cursor: pointer;
    transition: background 0.15s;
  }
  .stop-btn:hover { background: rgba(248,113,113,0.2); }

  .input-hint {
    font-size: 0.6875rem;
    color: var(--text-3);
    text-align: center;
    margin: 0.5rem 0 0;
  }
</style>
