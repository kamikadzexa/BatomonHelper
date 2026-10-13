"""Read-only model-v3 adapter for archived generations."""
import torch
from torch import nn
from torch.distributions import Categorical
ACTION_WIDTH=40
TOKEN_WIDTH=12
class LegacyPolicy(nn.Module):
    def __init__(self, width, species):
        super().__init__()
        self.core_width=width
        self.species = nn.Embedding(species + 1, 32)
        self.entity = nn.Sequential(nn.Linear(49, 64), nn.ReLU())
        self.state = nn.Sequential(nn.Linear(width - 270 + 15 * 64 + 128, 256), nn.ReLU(), nn.Linear(256, 256), nn.ReLU())
        self.action = nn.Sequential(nn.Linear(ACTION_WIDTH, 64), nn.ReLU())
        self.score = nn.Sequential(nn.Linear(320, 128), nn.ReLU(), nn.Linear(128, 1))
        self.value = nn.Linear(256, 1)
        self.tokens=nn.Sequential(nn.Linear(TOKEN_WIDTH,64),nn.ReLU())
        # Score the actual source and destination, rather than requiring the state
        # MLP to rediscover dynamic slot indexing from scalar action identifiers.
        self.context=nn.Sequential(nn.Linear(448,128),nn.ReLU(),nn.Linear(128,1))
        nn.init.zeros_(self.context[-1].weight); nn.init.zeros_(self.context[-1].bias)
    def forward(self, observations, actions, mask):
        entities = observations[:, 16:286].reshape(-1, 15, 18)
        entity_slots = self.entity(torch.cat([self.species(entities[:, :, 0].long()), entities[:, :, 1:]], dim=-1))
        entity = entity_slots.flatten(1)
        count=observations[:,self.core_width].long() if observations.shape[1]>self.core_width else torch.zeros(len(observations),device=observations.device,dtype=torch.long)
        if observations.shape[1]>self.core_width+1:
            tokens=observations[:,self.core_width+1:].reshape(len(observations),-1,TOKEN_WIDTH)
            encoded=self.tokens(tokens)
            valid=torch.arange(tokens.shape[1],device=observations.device)[None,:]<count[:,None]
            encoded=encoded*valid[:,:,None]
            pooled=torch.cat([encoded.sum(1)/count.clamp(min=1)[:,None],encoded.amax(1)],dim=-1)
        else:pooled=torch.zeros(len(observations),128,device=observations.device)
        state = self.state(torch.cat([observations[:, :16], entity, observations[:, 286:self.core_width],pooled], dim=-1))
        encoded_actions = self.action(actions)
        logits = self.score(torch.cat([state[:, None, :].expand(-1, actions.shape[1], -1), encoded_actions], dim=-1)).squeeze(-1)
        source=(actions[:,:,9]*11).round().long()-1
        target=(actions[:,:,10]*11).round().long()-1
        shop=actions[:,:,0]+actions[:,:,1]>0
        source=torch.where(shop,source+10,source)
        source_valid=(source>=0)&(source<15)&(shop|(source<10))
        target_valid=(target>=0)&(target<10)
        source_context=entity_slots.gather(1,source.clamp(0,14)[:,:,None].expand(-1,-1,64))*source_valid[:,:,None]
        target_context=entity_slots.gather(1,target.clamp(0,14)[:,:,None].expand(-1,-1,64))*target_valid[:,:,None]
        logits=logits+self.context(torch.cat([state[:,None,:].expand(-1,actions.shape[1],-1),encoded_actions,source_context,target_context],dim=-1)).squeeze(-1)
        # 90 equivalent move candidates must not crowd out a single Finish.
        kinds=actions[:,:,:9]*mask[:,:,None]
        kind_counts=kinds.sum(1)
        multiplicity=(kinds*kind_counts[:,None,:]).sum(-1).clamp(min=1)
        logits=logits-multiplicity.log()
        logits = logits.masked_fill(~mask, -torch.inf)
        return Categorical(logits=logits,validate_args=False), self.value(state).squeeze(-1)

