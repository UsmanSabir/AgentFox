<script lang="ts">
  import { onMount, tick } from 'svelte';
  import { streamChat } from '$lib/api';
  import {
    chatMessages, addUserMessage, addAssistantMessage,
    appendToken, finalizeMessage, activeConversationId, agentReady
  } from '$lib/stores';
  import {
    Send, RotateCcw, StopCircle, Bot, User, Copy, Check, Zap
  } from 'lucide-svelte';

  let inputEl: HTMLTextAreaElement;
  let scrollEl: HTMLDivElement;
  let message = '';
  let isStreaming = false;
  let abortCtrl: AbortController | null = null;
  let copiedId: string | null = null;

  $: messages     = $chatMessages;
  $: convId       = $activeConversationId;
  $: agentIsReady = $agentReady;

  onMount(() => {
    inputEl?.focus();
  });

  async function scrollToBottom() {
    await tick();
    if (scrollEl) scrollEl.scrollTop = scrollEl.scrollHeight;
  }

  async function send() {
    const text = message.trim();
    if (!text || isStreaming) return;

    message = '';
    addUserMessage(text);
    await scrollToBottom();

    const assistantId = addAssistantMessage('', true);
    isStreaming = true;
    abortCtrl   = new AbortController();

    try {
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
    }
  }

  function stop() {
    abortCtrl?.abort();
  }

  function clearChat() {
    chatMessages.set([]);
    activeConversationId.set(undefined);
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
  <!-- Conversation ID strip -->
  {#if convId}
    <div class="conv-strip">
      <span class="conv-label">Session:</span>
      <code class="conv-id">{convId}</code>
      <button class="icon-btn" on:click={clearChat} title="Clear conversation">
        <RotateCcw size={13} />
      </button>
    </div>
  {/if}

  <!-- Messages -->
  <div class="messages-wrap" bind:this={scrollEl}>
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
                <span class="message-role">{msg.role === 'user' ? 'You' : 'AgentFox'}</span>
                <span class="message-time">{msg.timestamp.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })}</span>
              </div>

              {#if msg.error}
                <div class="message-error">{msg.error}</div>
              {:else}
                <div
                  class="message-content prose"
                  class:stream-cursor={msg.streaming && msg.content.length > 0}
                >{msg.content}{#if msg.streaming && msg.content.length === 0}<span class="typing-dots"><span></span><span></span><span></span></span>{/if}</div>
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
    height: calc(100vh - var(--header-h));
    overflow: hidden;
  }

  /* Session strip */
  .conv-strip {
    display: flex;
    align-items: center;
    gap: 0.5rem;
    padding: 0.375rem 1.5rem;
    background: var(--surface);
    border-bottom: 1px solid var(--border);
    font-size: 0.75rem;
    color: var(--text-3);
  }
  .conv-label { color: var(--text-3); }
  .conv-id {
    font-family: monospace;
    background: var(--surface-2);
    padding: 0.1em 0.4em;
    border-radius: 4px;
    color: var(--text-2);
  }
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
    overflow-y: auto;
    padding: 1.5rem;
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

  .message-content {
    background: var(--surface);
    border: 1px solid var(--border);
    border-radius: 12px;
    padding: 0.75rem 1rem;
    font-size: 0.875rem;
    line-height: 1.65;
    color: var(--text);
    white-space: pre-wrap;
    word-break: break-word;
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
