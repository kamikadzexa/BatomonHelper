"""Typed relational actor-critic and explicit kind/conditional action probabilities."""
import torch
from torch import nn
from torch.distributions import Categorical

ACTION_WIDTH=40
TOKEN_WIDTH=64

class RelationalLayer(nn.Module):
    def __init__(self):
        super().__init__()
        self.query=nn.Linear(128,128);self.key=nn.Linear(128,128);self.value=nn.Linear(128,128)
        self.self_qkv=nn.Linear(128,384);self.output=nn.Linear(128,128)
        self.norm1=nn.LayerNorm(128);self.norm2=nn.LayerNorm(128);self.norm3=nn.LayerNorm(128)
        self.ff=nn.Sequential(nn.Linear(128,256),nn.GELU(),nn.Linear(256,128))
    @staticmethod
    def attention(q,k,v,valid=None):
        batch=q.shape[0]
        q=q.reshape(batch,-1,4,32).transpose(1,2)
        k=k.reshape(batch,-1,4,32).transpose(1,2)
        v=v.reshape(batch,-1,4,32).transpose(1,2)
        scores=q@k.transpose(-1,-2)/32**.5
        if valid is not None:scores=scores.masked_fill(~valid[:,None,None,:],-1e9)
        return (scores.softmax(-1)@v).transpose(1,2).reshape(batch,-1,128)
    def forward(self,nodes,tokens,valid):
        nodes=self.norm1(nodes+self.output(self.attention(self.query(nodes),self.key(tokens),self.value(tokens),valid)))
        q,k,v=self.self_qkv(nodes).chunk(3,-1)
        nodes=self.norm2(nodes+self.attention(q,k,v))
        return self.norm3(nodes+self.ff(nodes))

class Policy(nn.Module):
    def __init__(self,width,species,version=5):
        super().__init__();self.core_width=width;self.version=version;self.species_count=species
        self.species=nn.Embedding(species+1,32)
        self.entity=nn.Sequential(nn.Linear(49,128),nn.GELU())
        self.positions=nn.Embedding(16,128)
        self.header=nn.Linear(width-270,128)
        self.tokens=nn.Sequential(nn.Linear(TOKEN_WIDTH,128),nn.GELU())
        if version>=5:
            self.token_species=nn.Linear(32,128,bias=False)
            scale=torch.ones(ACTION_WIDTH)
            scale[[13,23,35,36]]=1/max(1,species);scale[[16,17,18]]=1/128
            scale[19]=1/64;scale[[20,27]]=1/9;scale[[21,26,30]]=1/100;scale[22]=1/32
            self.register_buffer('action_scale',scale)
            geometry=torch.zeros(16,5)
            for i in range(15):
                geometry[i,0 if i<6 else 1 if i<10 else 2]=1
                geometry[i,3]=i//3 if i<6 else 0;geometry[i,4]=(i%3)/2 if i<6 else (i-6)/3 if i<10 else (i-10)/4
            self.register_buffer('geometry',geometry);self.spatial=nn.Linear(5,128,bias=False)
        self.layers=nn.ModuleList([RelationalLayer(),RelationalLayer()])
        self.action=nn.Sequential(nn.Linear(ACTION_WIDTH,64),nn.GELU())
        self.score=nn.Sequential(nn.Linear(448,128),nn.GELU(),nn.Linear(128,1))
        self.kind=nn.Linear(128,9);self.value=nn.Linear(128,1)
    def forward(self,observations,actions,mask):
        batch=len(observations)
        entities=observations[:,16:286].reshape(batch,15,18)
        slots=self.entity(torch.cat([self.species(entities[:,:,0].long()),entities[:,:,1:]],-1))
        global_state=self.header(torch.cat([observations[:,:16],observations[:,286:self.core_width]],-1))
        nodes=torch.cat([slots,global_state[:,None,:]],1)+self.positions(torch.arange(16,device=observations.device))[None,:,:]
        if self.version>=5:nodes=nodes+self.spatial(self.geometry)[None,:,:]
        if observations.shape[1]>self.core_width+1:
            raw=observations[:,self.core_width+1:].reshape(batch,-1,TOKEN_WIDTH)
            count=observations[:,self.core_width].long()
            valid=torch.arange(raw.shape[1],device=observations.device)[None,:]<count[:,None]
        else:
            raw=torch.zeros(batch,1,TOKEN_WIDTH,device=observations.device)
            valid=torch.ones(batch,1,dtype=torch.bool,device=observations.device)
        if self.version>=5:
            numeric=raw.clone();numeric[:,:,0]=raw[:,:,0]/14;numeric[:,:,1]=raw[:,:,1]/32
            species_token=(raw[:,:,0]==1)|(raw[:,:,0]==13)
            ids=torch.where(species_token,raw[:,:,2],0).long().clamp(0,self.species_count)
            reference_scale=torch.where(raw[:,:,0]==2,32.,torch.where((raw[:,:,0]==4)|(raw[:,:,0]==5)|(raw[:,:,0]==7),128.,1.))
            numeric[:,:,2]=torch.where(species_token,raw[:,:,2]/max(1,self.species_count),raw[:,:,2]/reference_scale)
            encoded=self.tokens(numeric)+self.token_species(self.species(ids))*species_token[:,:,None]
        else:encoded=self.tokens(raw)
        for layer in self.layers:nodes=layer(nodes,encoded,valid)
        state=nodes[:,15,:];slots=nodes[:,:15,:]
        source=(actions[:,:,9]*11).round().long()-1;target=(actions[:,:,10]*11).round().long()-1
        shop=actions[:,:,0]+actions[:,:,1]>0;source=torch.where(shop,source+10,source)
        source_valid=(source>=0)&(source<15)&(shop|(source<10));target_valid=(target>=0)&(target<10)
        source_context=slots.gather(1,source.clamp(0,14)[:,:,None].expand(-1,-1,128))*source_valid[:,:,None]
        overflow_mask=(raw[:,:,1]==15)&valid
        overflow_context=(encoded*overflow_mask[:,:,None]).sum(1)/overflow_mask.sum(1).clamp(min=1)[:,None]
        source_context=torch.where(((~shop)&(source==10))[:,:,None],overflow_context[:,None,:],source_context)
        target_context=slots.gather(1,target.clamp(0,14)[:,:,None].expand(-1,-1,128))*target_valid[:,:,None]
        candidate=self.score(torch.cat([state[:,None,:].expand(-1,actions.shape[1],-1),self.action(actions*self.action_scale if self.version>=5 else actions),source_context,target_context],-1)).squeeze(-1)
        kinds=actions[:,:,:9].argmax(-1)
        membership=(kinds[:,:,None]==torch.arange(9,device=actions.device)[None,None,:])&mask[:,:,None]
        present=membership.any(1)
        kind_logits=self.kind(state).masked_fill(~present,-1e9);kind_log=kind_logits.log_softmax(-1)
        # One reduction over candidates for all nine kinds; avoid nine separate CUDA reductions.
        normalizers=candidate[:,:,None].expand(-1,-1,9).masked_fill(~membership,-1e9).logsumexp(1)
        joint=kind_log.gather(1,kinds)+candidate-normalizers.gather(1,kinds)
        joint=joint.masked_fill(~mask,-torch.inf)
        distribution=Categorical(logits=joint,validate_args=False);distribution.kind_logits=kind_logits
        return distribution,self.value(state).squeeze(-1)

def greedy_actions(distribution,features,mask,legacy=False):
    if legacy:return distribution.logits.argmax(-1)
    kinds=features[:,:,:9].argmax(-1)
    if hasattr(distribution,'kind_logits'):selected_kind=distribution.kind_logits.argmax(-1)
    else:
        log_kind=torch.stack([distribution.logits.masked_fill(~(mask&(kinds==k)),-torch.inf).logsumexp(-1) for k in range(9)],-1)
        selected_kind=log_kind.argmax(-1)
    return distribution.logits.masked_fill(~(mask&(kinds==selected_kind[:,None])),-torch.inf).argmax(-1)


def scenario_sample(distribution,seed,scenarios,days,ordinals):
    """Counter-based sampling independent of packing, workers and other episodes."""
    import numpy as np
    with np.errstate(over='ignore'):
        x=np.asarray(scenarios,dtype=np.uint64)^np.uint64(seed)
        x^=np.asarray(days,dtype=np.uint64)*np.uint64(0xBF58476D1CE4E5B9)
        x^=np.asarray(ordinals,dtype=np.uint64)*np.uint64(0x9E3779B97F4A7C15)
        x=(x^(x>>np.uint64(30)))*np.uint64(0xBF58476D1CE4E5B9)
        x=(x^(x>>np.uint64(27)))*np.uint64(0x94D049BB133111EB)
        x^=x>>np.uint64(31)
    uniform=(x>>np.uint64(11)).astype(np.float64)/(1<<53)
    cumulative=distribution.probs.double().cumsum(-1)
    return (cumulative<torch.as_tensor(uniform,device=cumulative.device)[:,None]).sum(-1).clamp(max=cumulative.shape[1]-1)
