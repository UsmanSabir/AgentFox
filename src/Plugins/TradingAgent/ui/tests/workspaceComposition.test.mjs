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
