import test from 'node:test';
import assert from 'node:assert/strict';
import { workspacePresetPanels } from '../src/workspaceComposition.ts';

test('focused modes cannot expose panels outside their column definitions', () => {
  const panels = ['list','chart','account','history'].map(id => ({id,title:id,region:'center',description:id}));
  const focused = {id:'focused',label:'Focused',active:{},columns:[
    {panels:['list'],active:'list'}, {panels:['account','history'],active:'account'}
  ]};
  assert.deepEqual(workspacePresetPanels(panels, focused).map(panel => panel.id), ['list','account','history']);
  assert.equal(workspacePresetPanels(panels, {id:'default',label:'Default',active:{}}), panels);
  assert.equal(workspacePresetPanels(panels), panels);
});

test('a layout may start an edge auto-hidden, and may keep every other panel on offer', async () => {
  const { presetPlacedPanels } = await import('../src/workspaceComposition.ts');
  const panels = ['list','chart','ledger','plan','logs'].map(id => ({id,title:id,region:'center',description:id}));
  const trading = {id:'trading',label:'Trading',active:{},columns:[
    {panels:['list'],active:'list'}, {panels:['chart'],active:'chart'}
  ], autoHide:{bottom:{panels:['ledger','plan'],active:'plan'}}};
  assert.deepEqual(presetPlacedPanels(trading), ['list','chart','ledger','plan']);
  assert.deepEqual(workspacePresetPanels(panels, trading).map(p => p.id), ['list','chart','ledger','plan'],
    'a columns layout still offers only what it places unless it says otherwise');
  assert.equal(workspacePresetPanels(panels, {...trading, offersUnplaced:true}), panels,
    'offersUnplaced keeps logs reachable from Panels');
});
