<script lang="ts">
  import { onMount, onDestroy, tick } from 'svelte';
import { streamChat, api, type SessionInfo, type SpecialistAgentInfo, type TodoSnapshot,
  type ToolActivity, type ToolActivityDetails, type ChatAttachment,
  type AttachmentCapabilities } from '$lib/api';
  import { renderMarkdown } from '$lib/markdown';
  import {
    chatMessages, addUserMessage, addAssistantMessage, addBackgroundResultMessage,
    appendToken, appendReasoning, setMessageStatus, upsertToolActivity, finalizeMessage,
    prepareMessageForRetry, attachReferences, setAssistantIndex, activeConversationId, activeAgentId, agentReady, resetChat,
    upsertPendingApproval, clearPendingApproval, type ChatMessage, type ChatAttachmentView
  } from '$lib/stores';
  import {
    Send, RotateCcw, StopCircle, Bot, User, Copy, Check, Zap, History, Plus, X,
    Download, Upload, Trash2, Pencil, Brain, GitFork, ChevronDown, Paperclip, FileText
  } from 'lucide-svelte';

  let inputEl: HTMLTextAreaElement;
  let scrollEl: HTMLDivElement;
  let message = '';
  let isStreaming = false;
  let abortCtrl: AbortController | null = null;
  let copiedId: string | null = null;
  let forkingMessageId: string | null = null;
  let pollTimer: ReturnType<typeof setInterval> | null = null;
  let sessions: SessionInfo[] = [];
  let specialists: SpecialistAgentInfo[] = [];
  let showSessions = false;
  let loadingSession = false;
  let importInput: HTMLInputElement;
  let globalMemoryEnabled = true;
  let todoSnapshot: TodoSnapshot | null = null;
  let sessionActivities: ToolActivity[] = [];
  let activityDetails: Record<string, ToolActivityDetails> = {};
  let showActivity = false;
  let loadingActivity = false;

  // ── Attachments ─────────────────────────────────────────────────────────
  // A pending attachment holds both the base64 payload we will POST and the view
  // metadata the transcript bubble renders, so send() never has to re-read the File.
  interface PendingAttachment {
    id: string;
    payload: ChatAttachment;
    view: ChatAttachmentView;
  }

  /** Images at or below this size get an inline thumbnail; larger ones show a chip. */
  const PREVIEW_MAX_BYTES = 2 * 1024 * 1024;

  let attachCaps: AttachmentCapabilities | null = null;
  let pendingAttachments: PendingAttachment[] = [];
  let attachInput: HTMLInputElement;
  let attachError = '';
  let dragDepth = 0;   // nested dragenter/dragleave pairs; only 0 means truly outside

  // Only the main agent's endpoints carry attachments; the specialist chat route is
  // text-only, so the paperclip disappears rather than silently dropping files.
  $: attachEnabled  = attachCaps?.enabled === true && selectedAgentId === 'main';
  $: if (!attachEnabled && pendingAttachments.length > 0) clearAttachments();
  $: isDraggingFile = dragDepth > 0;
  $: attachAccept   = attachCaps?.acceptedMediaTypes.join(',') ?? '';
  $: attachTitle    = attachCaps
    ? `Attach files — ${describeAccepted(attachCaps)} (max ${formatBytes(attachCaps.maxFileSizeBytes)} each, ` +
      `${attachCaps.maxFilesPerMessage} per message)`
    : 'Attach files';

  function describeAccepted(caps: AttachmentCapabilities): string {
    const kinds: string[] = [];
    if (caps.textFiles) kinds.push('text & code');
    if (caps.images)    kinds.push('images');
    if (caps.documents) kinds.push('PDFs');
    return kinds.join(', ') || 'nothing';
  }

  function formatBytes(bytes: number): string {
    if (bytes < 1024) return `${bytes} B`;
    if (bytes < 1024 * 1024) return `${Math.round(bytes / 1024)} KB`;
    return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
  }

  async function loadCapabilities() {
    // On failure leave attachments off: offering a button that always 400s is worse
    // than not offering one.
    try { attachCaps = (await api.capabilities()).attachments; }
    catch { attachCaps = null; }
  }

  function toBase64(file: File): Promise<string> {
    return new Promise((resolve, reject) => {
      const reader = new FileReader();
      reader.onerror = () => reject(new Error(`Could not read ${file.name}`));
      // readAsDataURL gives "data:<type>;base64,<payload>" — everything after the comma
      // is the raw base64 the API expects.
      reader.onload = () => resolve(String(reader.result).split(',')[1] ?? '');
      reader.readAsDataURL(file);
    });
  }

  /**
   * Client-side mirror of the server's limits. The server re-checks everything; this exists
   * so a mistake shows up next to the paperclip instead of costing a round-trip.
   */
  function rejectReason(file: File, alreadyPending: number): string | null {
    if (!attachCaps) return 'Attachments are unavailable.';
    if (alreadyPending >= attachCaps.maxFilesPerMessage)
      return `Up to ${attachCaps.maxFilesPerMessage} files per message.`;
    if (file.size > attachCaps.maxFileSizeBytes)
      return `${file.name} is ${formatBytes(file.size)} — the limit is ${formatBytes(attachCaps.maxFileSizeBytes)}.`;
    if (file.size === 0) return `${file.name} is empty.`;

    const isImage = file.type.startsWith('image/');
    const isPdf   = file.type === 'application/pdf' || file.name.toLowerCase().endsWith('.pdf');
    if (isImage && !attachCaps.images)
      return `${attachCaps.model || 'This model'} cannot read images.`;
    if (isPdf && !attachCaps.documents)
      return `${attachCaps.model || 'This model'} cannot read PDFs.`;
    if (!isImage && !isPdf && !attachCaps.textFiles)
      return `${file.name} is not an accepted file type.`;
    return null;
  }

  async function addFiles(files: FileList | File[] | null) {
    if (!files || !attachEnabled) return;
    attachError = '';

    const accepted: PendingAttachment[] = [];
    for (const file of Array.from(files)) {
      const reason = rejectReason(file, pendingAttachments.length + accepted.length);
      if (reason) { attachError = reason; continue; }

      let data: string;
      try { data = await toBase64(file); }
      catch (err) { attachError = err instanceof Error ? err.message : String(err); continue; }

      const mediaType = file.type || 'application/octet-stream';
      const showPreview = mediaType.startsWith('image/') && file.size <= PREVIEW_MAX_BYTES;

      accepted.push({
        id: crypto.randomUUID(),
        payload: { name: file.name, mediaType, data },
        view: {
          name: file.name,
          mediaType,
          size: file.size,
          previewUrl: showPreview ? `data:${mediaType};base64,${data}` : undefined
        }
      });
    }

    if (accepted.length > 0) pendingAttachments = [...pendingAttachments, ...accepted];
  }

  function removeAttachment(id: string) {
    pendingAttachments = pendingAttachments.filter(a => a.id !== id);
    attachError = '';
  }

  function clearAttachments() {
    pendingAttachments = [];
    attachError = '';
  }

  async function handleAttachInput(event: Event) {
    const input = event.currentTarget as HTMLInputElement;
    await addFiles(input.files);
    input.value = ''; // let the same file be picked again after removing it
  }

  // Pasting a screenshot is the fastest path to "look at this", so treat clipboard
  // files exactly like picked ones — but only when the model can actually use them.
  async function handlePaste(event: ClipboardEvent) {
    if (!attachEnabled) return;
    const files = Array.from(event.clipboardData?.files ?? []);
    if (files.length === 0) return;
    event.preventDefault();
    await addFiles(files);
  }

  function handleDragEnter(event: DragEvent) {
    if (!attachEnabled || !event.dataTransfer?.types.includes('Files')) return;
    dragDepth += 1;
  }

  function handleDragLeave() {
    if (dragDepth > 0) dragDepth -= 1;
  }

  async function handleDrop(event: DragEvent) {
    dragDepth = 0;
    if (!attachEnabled || !event.dataTransfer?.files.length) return;
    event.preventDefault();
    await addFiles(event.dataTransfer.files);
  }

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
    await Promise.all([loadSessions(), loadSpecialists(), loadMemorySettings(), loadCapabilities()]);
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
      if (data.pendingApproval) {
        upsertPendingApproval(data.pendingApproval);
      } else {
        clearPendingApproval();
      }
      await loadTodos(cid);
    } catch {
      // silently ignore poll errors (server may be restarting)
    }
  }

  async function loadTodos(cid = convId) {
    if (!cid) { todoSnapshot = null; return; }
    try { todoSnapshot = await api.todos(cid); } catch { todoSnapshot = null; }
  }

  async function loadActivity(cid = convId) {
    if (!cid) { sessionActivities = []; return; }
    try { sessionActivities = await api.activity(cid); }
    catch { sessionActivities = []; }
  }

  async function loadActivityDetails(activity: ToolActivity) {
    if (!convId || activityDetails[activity.callId]) return;
    try {
      const detail = await api.activityDetails(convId, activity.callId);
      activityDetails = { ...activityDetails, [activity.callId]: detail };
    } catch {
      // Leave the summary visible when details cannot be loaded.
    }
  }

  let respondingApprovalId: string | null = null;
  let approvalFeedback: Record<string, string> = {};

  async function respondToApproval(approvalId: string, approved: boolean) {
    respondingApprovalId = approvalId;
    try {
      if (approved) await api.hitlApprove(approvalId);
      else await api.hitlReject(approvalId, approvalFeedback[approvalId]?.trim() || undefined);
      clearPendingApproval(approvalId);
      const remaining = { ...approvalFeedback };
      delete remaining[approvalId];
      approvalFeedback = remaining;
    } catch {
      // leave the bubble in place — the next poll (or a retry click) can still resolve it
    } finally {
      respondingApprovalId = null;
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
    await new Promise<void>((resolve) => {
      requestAnimationFrame(() => {
        if (autoStick && scrollEl) scrollEl.scrollTop = scrollEl.scrollHeight;
        resolve();
      });
    });
  }

  async function send() {
    const text = message.trim();
    if ((!text && pendingAttachments.length === 0) || isStreaming) return;

    // Attachments are consumed by this turn: detach them from the composer before the
    // await so a second Enter press cannot send the same files twice.
    const attached = pendingAttachments;
    pendingAttachments = [];
    attachError = '';

    message = '';
    addUserMessage(text, attached.map(a => a.view));
    const assistantId = addAssistantMessage('', true, text);
    await runMessage(text, assistantId, attached.map(a => a.payload));
  }

  async function retryMessage(msg: ChatMessage) {
    if (!msg.error || !msg.retryContent || isStreaming || !agentIsReady) return;

    prepareMessageForRetry(msg.id);
    // Retry re-sends text only. The file bytes were dropped once the turn was handed off,
    // and silently retrying without them would be worse than making the user re-attach.
    await runMessage(msg.retryContent, msg.id);
  }

  async function runMessage(text: string, assistantId: string, attachments?: ChatAttachment[]) {
    isStreaming = true;
    abortCtrl   = new AbortController();
    await scrollToBottom(true);

    try {
      if (selectedAgentId === 'main') {
        const gen = streamChat(text, convId, abortCtrl.signal, attachments);
        let completed = false;
        for await (const event of gen) {
          if (event.type === 'token') {
            appendToken(assistantId, event.token);
            await scrollToBottom();
          } else if (event.type === 'session') {
            activeConversationId.set(event.conversationId);
            await loadTodos(event.conversationId);
            void pollPending(event.conversationId);
          } else if (event.type === 'reasoning') {
            appendReasoning(assistantId, event.text);
          } else if (event.type === 'status') {
            setMessageStatus(assistantId, event.status);
          } else if (event.type === 'tool_activity') {
            upsertToolActivity(assistantId, event.activity);
          } else if (event.type === 'done') {
            if (event.conversationId) activeConversationId.set(event.conversationId);
            attachReferences(assistantId, event.references);
            setAssistantIndex(assistantId, event.assistantIndex);
            finalizeMessage(assistantId);
            completed = true;
            await loadTodos(event.conversationId ?? convId);
            await loadActivity(event.conversationId ?? convId);
            break;
          } else if (event.type === 'error') {
            finalizeMessage(assistantId, event.error);
            completed = true;
            break;
          }
        }
        if (!completed && !abortCtrl.signal.aborted) {
          finalizeMessage(assistantId, 'The response ended before completion.');
        }
      } else {
        const response = await api.specialistChat(selectedAgentId, text, convId);
        if (response.conversationId) activeConversationId.set(response.conversationId);
        if (response.success) {
          appendToken(assistantId, response.response);
          attachReferences(assistantId, response.references);
          setAssistantIndex(assistantId, response.assistantIndex);
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
      await loadSessions();
      await loadTodos();
      await loadActivity();
      await scrollToBottom();
      await tick();
      inputEl?.focus();
    }
  }

  function stop() {
    abortCtrl?.abort();
  }

  function clearChat() {
    resetChat(selectedAgentId);
    clearAttachments();
    todoSnapshot = null;
    sessionActivities = [];
    activityDetails = {};
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
      // Always resume rather than trusting the sidebar's cached status: the background
      // idle timer can archive a session server-side after the last loadSessions() call,
      // and resuming an already-active session is a harmless no-op.
      await api.resumeSession(session.id);
      const history = await api.sessionMessages(session.id);
      activeAgentId.set(specialists.some(agent => agent.id === history.agentId) ? history.agentId : 'main');
      activeConversationId.set(history.conversationId);
      todoSnapshot = null;
      activityDetails = {};
      chatMessages.set(history.messages.map(item => ({
        id: crypto.randomUUID(),
        role: item.role,
        content: item.content,
        agentAddition: item.agentAddition,
        references: item.references,
        assistantIndex: item.assistantIndex,
        timestamp: new Date(session.lastActive)
      })));
      showSessions = false;
      await loadSessions();
      await loadTodos(history.conversationId);
      await loadActivity(history.conversationId);
      await scrollToBottom(true);
    } catch (err) {
      alert('Failed to load session: ' + (err instanceof Error ? err.message : String(err)));
    } finally {
      loadingSession = false;
    }
  }

  async function exportSession(session: SessionInfo, event: Event) {
    event.stopPropagation();
    try {
      await api.exportSession(session.id);
    } catch (err) {
      alert('Export failed: ' + (err instanceof Error ? err.message : String(err)));
    }
  }

  async function loadMemorySettings() {
    try { globalMemoryEnabled = (await api.memorySettings()).globalEnabled; }
    catch { globalMemoryEnabled = true; }
  }

  async function renameSession(session: SessionInfo, event: Event) {
    event.stopPropagation();
    if (isStreaming || loadingSession) return;

    const title = prompt('Rename session', session.title ?? '');
    if (title === null) return;
    if (!title.trim()) {
      alert('Session name cannot be empty.');
      return;
    }
    if (title.trim().length > 120) {
      alert('Session name cannot exceed 120 characters.');
      return;
    }

    try {
      await api.renameSession(session.id, title.trim());
      await loadSessions();
    } catch (err) {
      alert('Rename failed: ' + (err instanceof Error ? err.message : String(err)));
    }
  }

  async function toggleSessionMemory(session: SessionInfo, event: Event) {
    event.stopPropagation();
    if (!globalMemoryEnabled || isStreaming || loadingSession) return;

    try {
      await api.setSessionMemory(session.id, !session.memoryEnabled);
      await loadSessions();
    } catch (err) {
      alert('Memory setting failed: ' + (err instanceof Error ? err.message : String(err)));
    }
  }

  async function deleteSession(session: SessionInfo, event: Event) {
    event.stopPropagation();
    if (isStreaming || loadingSession) return;
    if (!confirm(`Delete session "${session.id}"? This permanently removes its transcript and cannot be undone.`))
      return;
    try {
      await api.deleteSession(session.id);
      if (session.id === convId) resetChat(selectedAgentId);
      await loadSessions();
    } catch (err) {
      alert('Delete failed: ' + (err instanceof Error ? err.message : String(err)));
    }
  }

  function triggerImport() {
    importInput?.click();
  }

  async function handleImportFile(event: Event) {
    const input = event.currentTarget as HTMLInputElement;
    const file = input.files?.[0];
    input.value = ''; // allow re-importing the same file later
    if (!file || isStreaming) return;

    let newId: string | null = null;
    try {
      const envelope = JSON.parse(await file.text());
      const result = await api.importSession(envelope);
      newId = result.conversationId;
      await loadSessions();
    } catch (err) {
      alert('Import failed: ' + (err instanceof Error ? err.message : String(err)));
      return;
    }

    const imported = sessions.find(s => s.id === newId);
    if (imported) await openSession(imported);
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

  function autoResize(node: HTMLTextAreaElement, _value?: string) {
    function resize() {
      node.style.height = 'auto';
      node.style.height = Math.min(node.scrollHeight, 160) + 'px';
    }
    resize();
    node.addEventListener('input', resize);
    return {
      // Re-run when the bound value changes programmatically (e.g. cleared on send),
      // since that does not fire an 'input' event. tick() ensures the DOM value is
      // updated before we measure scrollHeight.
      update() { tick().then(resize); },
      destroy() { node.removeEventListener('input', resize); },
    };
  }

  async function forkFromMessage(msg: ChatMessage) {
    if (!convId || msg.assistantIndex === undefined || isStreaming || loadingSession ||
        forkingMessageId !== null)
      return;

    forkingMessageId = msg.id;
    try {
      const result = await api.forkSession(convId, msg.assistantIndex);
      await loadSessions();
      const forked = sessions.find(session => session.id === result.conversationId);
      if (!forked)
        throw new Error('The fork was created but could not be found in the session list.');
      await openSession(forked);
    } catch (err) {
      alert('Fork failed: ' + (err instanceof Error ? err.message : String(err)));
    } finally {
      forkingMessageId = null;
    }
  }

  function isSafeReferenceUrl(url: string): boolean {
    try {
      const parsed = new URL(url);
      return parsed.protocol === 'http:' || parsed.protocol === 'https:';
    } catch {
      return false;
    }
  }

  function statusLabel(status?: string): string {
    return ({
      thinking: 'Thinking…',
      running_tools: 'Running tools…',
      preparing_response: 'Preparing response…'
    } as Record<string, string>)[status ?? ''] ?? '';
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
          <div class="session-head-actions">
            <button class="toolbar-btn" on:click={triggerImport} title="Import a session from a file">
              <Upload size={13} /> Import
            </button>
            <button class="icon-btn" on:click={() => showSessions = false} title="Close"><X size={15} /></button>
          </div>
        </div>
        <input
          type="file"
          accept="application/json,.json"
          bind:this={importInput}
          on:change={handleImportFile}
          hidden
        />
        <div class="session-list">
          {#if sessions.length === 0}
            <p class="session-empty">No saved sessions yet.</p>
          {:else}
            {#each sessions as session (session.id)}
              <div class="session-row" class:active={session.id === convId}>
                <button
                  class="session-item"
                  on:click={() => openSession(session)}
                  disabled={loadingSession || isStreaming}
                >
                  <span class="session-item-title">{session.title ?? session.id}</span>
                  <span class="session-item-meta">{session.title ? `${session.id} · ` : ''}{session.origin} · {session.status}</span>
                  <span class="session-item-time">{new Date(session.lastActive).toLocaleString()}</span>
                </button>
                <div class="session-actions">
                  <button
                    class="icon-btn"
                    class:memory-on={session.memoryEnabled}
                    on:click={(e) => toggleSessionMemory(session, e)}
                    disabled={!globalMemoryEnabled || isStreaming || loadingSession}
                    aria-pressed={session.memoryEnabled}
                    title={!globalMemoryEnabled
                      ? 'Memory is disabled globally'
                      : session.memoryEnabled
                        ? 'Disable memory for this session'
                        : 'Enable memory for this session'}
                  >
                    <Brain size={14} />
                  </button>
                  <button
                    class="icon-btn"
                    on:click={(e) => renameSession(session, e)}
                    disabled={isStreaming || loadingSession}
                    title="Rename session"
                  >
                    <Pencil size={14} />
                  </button>
                  <button
                    class="icon-btn"
                    on:click={(e) => exportSession(session, e)}
                    title="Export session to file"
                  >
                    <Download size={14} />
                  </button>
                  <button
                    class="icon-btn danger"
                    on:click={(e) => deleteSession(session, e)}
                    disabled={isStreaming || loadingSession}
                    title="Delete session"
                  >
                    <Trash2 size={14} />
                  </button>
                </div>
              </div>
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
              {:else if msg.pendingApproval}
                <div class="approval-card">
                  <div class="approval-desc">🔐 {msg.pendingApproval.description}</div>
	                  {#if msg.pendingApproval.details}
	                    <div class="approval-details">{msg.pendingApproval.details}</div>
	                  {/if}
	                  <input
	                    class="approval-feedback"
	                    value={approvalFeedback[msg.pendingApproval.approvalId] ?? ''}
	                    on:input={(event) => {
	                      approvalFeedback = {
	                        ...approvalFeedback,
	                        [msg.pendingApproval!.approvalId]: (event.currentTarget as HTMLInputElement).value
	                      };
	                    }}
	                    placeholder="Optional rejection feedback"
	                  />
	                  <div class="approval-actions">
                    <button
                      class="approval-btn approve"
                      disabled={respondingApprovalId === msg.pendingApproval.approvalId}
                      on:click={() => msg.pendingApproval && respondToApproval(msg.pendingApproval.approvalId, true)}
                    >✅ Approve</button>
                    <button
                      class="approval-btn reject"
                      disabled={respondingApprovalId === msg.pendingApproval.approvalId}
                      on:click={() => msg.pendingApproval && respondToApproval(msg.pendingApproval.approvalId, false)}
                    >❌ Reject</button>
                  </div>
                </div>
              {:else if msg.role === 'user'}
                {#if msg.attachments && msg.attachments.length > 0}
                  <div class="message-attachments">
                    {#each msg.attachments as file}
                      {#if file.previewUrl}
                        <img class="message-attachment-image" src={file.previewUrl} alt={file.name} title={file.name} />
                      {:else}
                        <span class="attach-chip static">
                          <FileText size={14} class="attach-icon" />
                          <span class="attach-name" title={file.name}>{file.name}</span>
                          <span class="attach-size">{formatBytes(file.size)}</span>
                        </span>
                      {/if}
                    {/each}
                  </div>
                {/if}
                {#if msg.content}
                  <div class="message-content user-text">{msg.content}</div>
                {/if}
                {#if msg.agentAddition}
                  <details class="agent-addition">
                    <summary>
                      <span>Agent-added context</span>
                      <span class="agent-addition-hint">not typed by you</span>
                    </summary>
                    <div class="agent-addition-content">{msg.agentAddition}</div>
                  </details>
                {/if}
	              {:else}
	                <div
	                  class="message-content markdown"
	                  class:stream-cursor={msg.streaming && msg.content.length > 0}
	                >{#if msg.content.length > 0}{@html renderMarkdown(msg.content)}{:else if msg.streaming}<span class="typing-dots"><span></span><span></span><span></span></span>{/if}</div>
	              {/if}

	              {#if msg.role === 'assistant' && (msg.reasoning || (msg.streaming && msg.status))}
	                <details class="aux-panel" open={false}>
	                  <summary>{msg.reasoning ? 'Reasoning' : statusLabel(msg.status)}</summary>
	                  {#if msg.reasoning}
	                    <div class="reasoning-text">{@html renderMarkdown(msg.reasoning)}</div>
	                  {:else}
	                    <div class="status-text">{statusLabel(msg.status)}</div>
	                  {/if}
	                </details>
	              {/if}

	              {#if msg.role === 'assistant' && msg.toolActivities && msg.toolActivities.length > 0}
	                <div class="tool-summary">
	                  {#each msg.toolActivities as activity}
	                    <span class="tool-chip">{activity.toolName || 'tool'} · {activity.status}</span>
	                  {/each}
	                </div>
              {/if}

              {#if msg.role === 'assistant' && !msg.streaming && !msg.error && msg.references && msg.references.length > 0}
                <details class="sources">
                  <summary class="sources-summary">
                    <span class="sources-label">Sources</span>
                    <span class="sources-count">{msg.references.length}</span>
                    <span class="sources-chevron"><ChevronDown size={14} /></span>
                  </summary>
	                  <ul class="sources-list">
	                    {#each msg.references as ref}
	                      <li>
	                        {#if isSafeReferenceUrl(ref.url)}
	                          <a href={ref.url} target="_blank" rel="noopener noreferrer" title={ref.url}>
	                            {ref.title || ref.url}
	                          </a>
	                        {:else}
	                          <span>{ref.title || ref.url}</span>
	                        {/if}
                        {#if ref.source}<span class="sources-src">· {ref.source}</span>{/if}
                      </li>
                    {/each}
                  </ul>
                </details>
              {/if}

              {#if !msg.streaming && (msg.role === 'user' || (msg.role === 'assistant' && (!msg.error || msg.retryContent)))}
                <div class="message-actions">
                  {#if msg.role === 'assistant' && !msg.error && msg.assistantIndex !== undefined && convId}
                    <button
                      class="message-action-btn"
                      on:click={() => forkFromMessage(msg)}
                      disabled={isStreaming || loadingSession || forkingMessageId !== null}
                      title="Start a new session from this response"
                    >
                      <GitFork size={12} />
                      <span>{forkingMessageId === msg.id ? 'Forking…' : 'Fork from here'}</span>
                    </button>
                  {/if}
                  {#if msg.role === 'assistant' && msg.error && msg.retryContent}
                    <button
                      class="message-action-btn retry"
                      on:click={() => retryMessage(msg)}
                      disabled={isStreaming || !agentIsReady}
                      title="Retry this message"
                    >
                      <RotateCcw size={12} />
                      <span>Retry</span>
                    </button>
                  {:else}
                    <button
                      class="message-action-btn"
                      on:click={() => copyContent(msg.id, msg.content)}
                      title={msg.role === 'user' ? 'Copy message' : 'Copy response'}
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
	    {#if todoSnapshot?.enabled && todoSnapshot.items.length > 0}
	      <details class="progress-panel">
	        <summary>
	          <span>Progress</span>
	          <span>{todoSnapshot.items.length - todoSnapshot.remainingCount}/{todoSnapshot.items.length} complete</span>
	        </summary>
	        {#if todoSnapshot.plan}
	          <div class="plan-preview">{todoSnapshot.plan}</div>
	        {/if}
	        <ul class="todo-list">
	          {#each todoSnapshot.items as item}
	            <li class:todo-complete={item.completed}>
	              <span>{item.completed ? '✓' : '○'}</span>{item.title}
	            </li>
	          {/each}
	        </ul>
	      </details>
	    {/if}

	    {#if sessionActivities.length > 0}
	      <details class="progress-panel activity-panel" bind:open={showActivity} on:toggle={() => {
	        if (showActivity && !loadingActivity) {
	          loadingActivity = true;
	          Promise.all(sessionActivities.map(loadActivityDetails)).finally(() => loadingActivity = false);
	        }
	        }}>
	        <summary>
	          <span class="progress-label">
	            <ChevronDown
	              size={13}
	              class={showActivity ? 'activity-chevron open' : 'activity-chevron'}
	              aria-hidden="true"
	            />
	            <span>Tool activity</span>
	          </span>
	          <span>{sessionActivities.length} call{sessionActivities.length === 1 ? '' : 's'}</span>
	        </summary>
	        <ul class="activity-list">
	          {#each sessionActivities as activity}
	            {@const detail = activityDetails[activity.callId]}
	            <li>
	              <div class="activity-row">
	                <span>{activity.toolName || 'tool'}</span>
	                <span>{activity.status}</span>
	              </div>
	              {#if detail}
	                <pre>{JSON.stringify({ arguments: detail.arguments, result: detail.result }, null, 2)}</pre>
	              {/if}
	            </li>
	          {/each}
	        </ul>
	      </details>
	    {/if}

	    {#if attachError}
	      <div class="attach-error" role="alert">
	        <span>{attachError}</span>
	        <button class="attach-error-dismiss" on:click={() => attachError = ''} title="Dismiss">
	          <X size={12} />
	        </button>
	      </div>
	    {/if}

	    {#if pendingAttachments.length > 0}
	      <div class="attach-tray">
	        {#each pendingAttachments as item (item.id)}
	          <div class="attach-chip">
	            {#if item.view.previewUrl}
	              <img class="attach-thumb" src={item.view.previewUrl} alt="" />
	            {:else}
	              <FileText size={14} class="attach-icon" />
	            {/if}
	            <span class="attach-name" title={item.view.name}>{item.view.name}</span>
	            <span class="attach-size">{formatBytes(item.view.size)}</span>
	            <button
	              class="attach-remove"
	              on:click={() => removeAttachment(item.id)}
	              disabled={isStreaming}
	              title="Remove {item.view.name}"
	            >
	              <X size={12} />
	            </button>
	          </div>
	        {/each}
	      </div>
	    {/if}

	    <div
	      class="input-wrap"
	      class:drop-active={isDraggingFile}
	      role="presentation"
	      on:dragenter={handleDragEnter}
	      on:dragleave={handleDragLeave}
	      on:dragover|preventDefault
	      on:drop|preventDefault={handleDrop}
	    >
      {#if isDraggingFile}
        <div class="drop-overlay">Drop files to attach</div>
      {/if}

      <textarea
        bind:this={inputEl}
        bind:value={message}
        use:autoResize={message}
        on:keydown={handleKeyDown}
        on:paste={handlePaste}
        placeholder={agentIsReady ? 'Message AgentFox… (Enter to send, Shift+Enter for newline)' : 'Waiting for agent…'}
        disabled={!agentIsReady}
        rows="1"
        class="chat-input"
      ></textarea>

      <div class="input-actions">
        {#if attachEnabled}
          <input
            type="file"
            multiple
            accept={attachAccept}
            bind:this={attachInput}
            on:change={handleAttachInput}
            class="hidden-input"
          />
          <button
            class="icon-btn"
            on:click={() => attachInput?.click()}
            title={attachTitle}
            disabled={isStreaming || !agentIsReady}
          >
            <Paperclip size={15} />
          </button>
        {/if}

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
            disabled={(!message.trim() && pendingAttachments.length === 0) || !agentIsReady}
            title="Send message"
          >
            <Send size={15} />
          </button>
        {/if}
      </div>
    </div>
    <p class="input-hint">
      AgentFox can use tools, access memory, and spawn sub-agents.
      {#if attachEnabled && attachCaps}
        · Attach {describeAccepted(attachCaps)} — drag, paste, or use the paperclip.
      {/if}
    </p>
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
  .session-list { overflow-y: auto; padding: 0.45rem; display: flex; flex-direction: column; gap: 0.15rem; }
  .session-head-actions { display: flex; align-items: center; gap: 0.35rem; }

  .session-row {
    display: flex;
    align-items: stretch;
    gap: 0.2rem;
    border: 1px solid transparent;
    border-radius: 7px;
  }
  .session-row:hover { background: var(--surface-2); border-color: var(--border); }
  .session-row.active { background: var(--primary-dim); border-color: rgba(129,140,248,0.35); }

  .session-item {
    flex: 1;
    min-width: 0;
    text-align: left;
    display: flex;
    flex-direction: column;
    gap: 0.2rem;
    border: none;
    border-radius: 7px;
    background: transparent;
    color: var(--text);
    padding: 0.65rem;
    cursor: pointer;
  }
  .session-item:disabled { opacity: 0.55; cursor: wait; }

  .session-actions {
    display: flex;
    flex-direction: column;
    align-items: center;
    justify-content: center;
    gap: 0.1rem;
    padding: 0.3rem 0.35rem 0.3rem 0;
  }
  .session-actions .icon-btn { padding: 0.25rem; }
  .session-actions .icon-btn.memory-on { color: var(--success); }
  .session-actions .icon-btn.danger:hover { color: var(--danger); }
  .session-item-title { font-size: 0.75rem; font-weight: 600; word-break: break-word; }
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

  .agent-addition {
    margin-top: 0.4rem;
    max-width: 100%;
    border: 1px solid var(--border-md);
    border-radius: 8px;
    background: color-mix(in srgb, var(--surface-2) 72%, transparent);
    color: var(--text-2);
    font-size: 0.75rem;
  }

  .agent-addition summary {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 0.75rem;
    padding: 0.45rem 0.65rem;
    cursor: pointer;
    user-select: none;
    font-weight: 600;
  }

  .agent-addition-hint {
    color: var(--text-3);
    font-size: 0.6875rem;
    font-weight: 400;
  }

  .agent-addition-content {
    padding: 0.6rem 0.7rem;
    border-top: 1px solid var(--border);
    white-space: pre-wrap;
    font-family: var(--font-mono, ui-monospace, SFMono-Regular, Menlo, monospace);
    line-height: 1.5;
    overflow-wrap: anywhere;
  }

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

  .approval-card {
    background: rgba(250,204,21,0.08);
    border: 1px solid rgba(250,204,21,0.3);
    border-radius: 8px;
    padding: 0.75rem 0.875rem;
    font-size: 0.8125rem;
  }
  .approval-desc {
    font-weight: 600;
    color: var(--text-1);
    margin-bottom: 0.25rem;
  }
  .approval-details {
    color: var(--text-3);
    font-size: 0.75rem;
    margin-bottom: 0.625rem;
    word-break: break-word;
  }
  .approval-actions {
    display: flex;
    gap: 0.5rem;
  }
  .approval-feedback {
    width: 100%;
    margin: 0.25rem 0 0.625rem;
    padding: 0.4rem 0.5rem;
    border: 1px solid var(--border);
    border-radius: 6px;
    background: var(--surface-2);
    color: var(--text);
    font: inherit;
    font-size: 0.75rem;
  }
  .approval-btn {
    font-size: 0.8125rem;
    font-weight: 600;
    padding: 0.375rem 0.75rem;
    border-radius: 6px;
    border: 1px solid var(--border);
    background: var(--surface-2);
    color: var(--text-1);
    cursor: pointer;
  }
  .approval-btn:hover:not(:disabled) {
    filter: brightness(1.1);
  }
  .approval-btn:disabled {
    opacity: 0.5;
    cursor: default;
  }
  .approval-btn.approve {
    border-color: rgba(74,222,128,0.4);
    color: #4ade80;
  }
  .approval-btn.reject {
    border-color: rgba(248,113,113,0.4);
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

  /* Sources */
  .sources {
    margin-top: 0.5rem;
    padding-top: 0.5rem;
    border-top: 1px solid var(--border);
    font-size: 0.75rem;
  }
  .sources-summary {
    display: inline-flex;
    align-items: center;
    gap: 0.35rem;
    color: var(--text-3);
    cursor: pointer;
    list-style: none;
    user-select: none;
  }
  .sources-summary::-webkit-details-marker { display: none; }
  .sources-summary:hover { color: var(--text-2); }
  .sources-label {
    text-transform: uppercase;
    letter-spacing: 0.04em;
    font-size: 0.625rem;
  }
  .sources-count {
    min-width: 1.25rem;
    padding: 0.05rem 0.35rem;
    border: 1px solid var(--border);
    border-radius: 99px;
    text-align: center;
    font-size: 0.625rem;
    line-height: 1.2;
  }
  .sources-chevron { transition: transform 0.15s ease; }
  .sources[open] .sources-chevron { transform: rotate(180deg); }
  .sources-list { list-style: none; margin: 0.4rem 0 0; padding: 0; }
  .sources-list li { margin: 0.15rem 0; overflow-wrap: anywhere; }
  .sources-list a { color: var(--primary); text-decoration: none; }
  .sources-list a:hover { text-decoration: underline; }
  .sources-src { color: var(--text-3); }

  .aux-panel {
    margin-top: 0.45rem;
    border: 1px solid var(--border);
    border-radius: 7px;
    background: var(--surface-2);
    font-size: 0.75rem;
  }
  .aux-panel summary,
  .progress-panel summary {
    cursor: pointer;
    display: flex;
    justify-content: space-between;
    gap: 0.75rem;
    padding: 0.45rem 0.625rem;
    color: var(--text-2);
    user-select: none;
  }
  .reasoning-text,
  .status-text {
    padding: 0.5rem 0.625rem;
    border-top: 1px solid var(--border);
    color: var(--text-3);
    line-height: 1.5;
  }
  .tool-summary {
    display: flex;
    flex-wrap: wrap;
    gap: 0.3rem;
    margin-top: 0.35rem;
  }
  .tool-chip {
    border: 1px solid var(--border);
    border-radius: 99px;
    padding: 0.15rem 0.45rem;
    color: var(--text-3);
    font-size: 0.6875rem;
  }

  /* Message actions */
  .message-actions {
    display: flex;
    align-items: center;
    gap: 0.75rem;
    margin-top: 0.125rem;
  }

  .message-action-btn {
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
  }
  .message-action-btn:hover { color: var(--text-2); }
  .message-action-btn.retry { color: var(--danger); }
  .message-action-btn.retry:hover { filter: brightness(1.15); }
  .message-action-btn:disabled { cursor: wait; opacity: 0.55; }

  /* Input bar */
  .input-bar {
    padding: 1rem 1.5rem 0.75rem;
    border-top: 1px solid var(--border);
    background: var(--surface);
    flex-shrink: 0;
  }

  .progress-panel {
    max-width: 780px;
    margin: 0 auto 0.5rem;
    border: 1px solid var(--border);
    border-radius: 8px;
    background: var(--surface-2);
    font-size: 0.75rem;
  }
  .progress-panel summary span:last-child {
    color: var(--text-3);
  }
  .progress-label {
    display: inline-flex;
    align-items: center;
    gap: 0.35rem;
  }
  .activity-chevron {
    color: var(--text-3);
    transition: transform 0.15s ease;
  }
  .activity-chevron.open { transform: rotate(180deg); }
  .plan-preview {
    max-height: 120px;
    overflow-y: auto;
    padding: 0.5rem 0.625rem;
    border-top: 1px solid var(--border);
    color: var(--text-3);
    white-space: pre-wrap;
  }
  .todo-list,
  .activity-list {
    list-style: none;
    margin: 0;
    padding: 0.35rem 0.625rem 0.55rem;
    border-top: 1px solid var(--border);
  }
  .todo-list li {
    display: flex;
    gap: 0.45rem;
    padding: 0.2rem 0;
    color: var(--text-2);
  }
  .todo-list li.todo-complete {
    color: var(--text-3);
    text-decoration: line-through;
  }
  .activity-list li {
    padding: 0.35rem 0;
    border-bottom: 1px solid var(--border);
  }
  .activity-list li:last-child { border-bottom: none; }
  .activity-row {
    display: flex;
    justify-content: space-between;
    gap: 0.5rem;
    color: var(--text-2);
  }
  .activity-list pre {
    max-height: 180px;
    overflow: auto;
    margin: 0.35rem 0 0;
    padding: 0.45rem;
    border-radius: 5px;
    background: var(--surface);
    color: var(--text-3);
    font-size: 0.6875rem;
    white-space: pre-wrap;
    overflow-wrap: anywhere;
  }

  .input-wrap {
    position: relative;
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
  .input-wrap.drop-active { border-color: var(--primary); border-style: dashed; }

  /* Attachments */
  .hidden-input { display: none; }

  .drop-overlay {
    position: absolute;
    inset: 0;
    display: flex;
    align-items: center;
    justify-content: center;
    border-radius: var(--radius);
    background: var(--surface-2);
    color: var(--primary);
    font-size: 0.8125rem;
    font-weight: 600;
    pointer-events: none;   /* the drop must land on .input-wrap underneath */
    z-index: 2;
  }

  .attach-tray {
    display: flex;
    flex-wrap: wrap;
    gap: 0.375rem;
    margin-bottom: 0.5rem;
  }

  .attach-chip {
    display: inline-flex;
    align-items: center;
    gap: 0.375rem;
    max-width: 260px;
    padding: 0.25rem 0.375rem 0.25rem 0.5rem;
    background: var(--surface-2);
    border: 1px solid var(--border-md);
    border-radius: 6px;
    font-size: 0.75rem;
    color: var(--text-2);
  }
  .attach-chip.static { padding-right: 0.5rem; }

  .attach-thumb {
    width: 22px;
    height: 22px;
    object-fit: cover;
    border-radius: 3px;
    flex-shrink: 0;
  }

  .attach-name {
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }
  .attach-size { color: var(--text-3); flex-shrink: 0; }

  .attach-remove {
    display: flex;
    align-items: center;
    background: transparent;
    border: none;
    cursor: pointer;
    color: var(--text-3);
    padding: 0.125rem;
    border-radius: 3px;
    flex-shrink: 0;
  }
  .attach-remove:hover { color: var(--danger); }
  .attach-remove:disabled { opacity: 0.4; cursor: not-allowed; }

  .attach-error {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 0.5rem;
    margin-bottom: 0.5rem;
    padding: 0.375rem 0.5rem;
    background: rgba(248,113,113,0.1);
    border: 1px solid rgba(248,113,113,0.25);
    border-radius: 6px;
    font-size: 0.75rem;
    color: var(--danger);
  }
  .attach-error-dismiss {
    display: flex;
    background: transparent;
    border: none;
    cursor: pointer;
    color: inherit;
    padding: 0.125rem;
  }

  .message-attachments {
    display: flex;
    flex-wrap: wrap;
    align-items: flex-start;
    gap: 0.375rem;
    margin-bottom: 0.375rem;
  }

  .message-attachment-image {
    max-width: 240px;
    max-height: 240px;
    border-radius: 8px;
    border: 1px solid var(--border);
  }

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
