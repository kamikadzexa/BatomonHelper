import unittest
import numpy as np
import torch
from learner import Policy, padded, pad_observations, ACTION_WIDTH, resume_environment_count, advantages, wilson, training_device, load_policy, optimizer_for, pack_rollout, optimize, PopulationInference, exploration_entropy, select_calibration, ranking_key

class LearnerTests(unittest.TestCase):
    def test_calibration_rejects_unsafe_candidates_and_ranking_uses_stable_tiebreaks(self):
        rows=[dict(minibatch=512,accepted=True,samples_per_second=100),dict(minibatch=1024,accepted=False,samples_per_second=300),dict(minibatch=256,accepted=True,samples_per_second=90)]
        self.assertEqual(select_calibration(rows,256),512)
        self.assertEqual(select_calibration([dict(minibatch=512,accepted=False)],256),256)
        better=dict(score=.4,average_wins=7,unused_deployable_bench=.3)
        worse=dict(score=.4,average_wins=7,unused_deployable_bench=.5)
        self.assertGreater(ranking_key(better),ranking_key(worse))
    def test_vectorized_population_retains_policy_assignment_and_every_candidate(self):
        for device in ['cpu']+(['cuda'] if torch.cuda.is_available() else []):
            torch.manual_seed(19);models=[Policy(287,20).to(device) for _ in range(4)]
            observations=np.zeros((5,288+24),np.float32);observations[:,287]=2;observations[:,288:]=.2
            observations[:,16]=np.arange(1,6)
            actions=[]
            for j in range(5):
                row=np.zeros((j+1,ACTION_WIDTH),np.float32);row[:,0]=1;row[:,9]=1/11;row[:,10]=1/11;actions.append(row)
            with torch.inference_mode():
                distribution,values=PopulationInference(models)(observations,actions)
                for j in range(5):
                    features,mask=padded([actions[j]],device)
                    expected,value=models[j%4](torch.as_tensor(observations[j:j+1],device=device),features,mask)
                    torch.testing.assert_close(distribution.probs[j,:j+1],expected.probs[0],atol=1e-5,rtol=1e-5)
                    torch.testing.assert_close(values[j],value[0],atol=1e-5,rtol=1e-5)
                    self.assertEqual(distribution.probs[j,j+1:].sum().item(),0)

    def test_candidate_multiplicity_does_not_bias_action_kind(self):
        model=Policy(287,20)
        for parameter in model.parameters():torch.nn.init.zeros_(parameter)
        actions=np.zeros((91,ACTION_WIDTH),np.float32);actions[:90,3]=1;actions[90,8]=1
        features,mask=padded([actions],'cpu')
        distribution,_=model(torch.zeros(1,287),features,mask)
        self.assertAlmostEqual(distribution.probs[0,:90].sum().item(),.5,places=6)
        self.assertAlmostEqual(distribution.probs[0,90].item(),.5,places=6)

    def test_entropy_does_not_reward_more_equivalent_move_candidates(self):
        entropies=[]
        for moves in (2,90):
            actions=np.zeros((moves+1,ACTION_WIDTH),np.float32);actions[:moves,3]=1;actions[moves,8]=1
            features,mask=padded([actions],'cpu')
            probabilities=torch.tensor([[.5/moves]*moves+[.5]],dtype=torch.float32)
            distribution=torch.distributions.Categorical(probs=probabilities)
            entropies.append(exploration_entropy(distribution,features,mask).item())
        self.assertAlmostEqual(entropies[0],entropies[1],places=6)

    def test_existing_checkpoint_migrates_without_discarding_actor(self):
        source=Policy(287,20);weights={k:v for k,v in source.state_dict().items() if not k.startswith('context.')}
        target=Policy(287,20);load_policy(target,weights)
        self.assertTrue(torch.equal(source.score[0].weight,target.score[0].weight))
        self.assertEqual(target.context[-1].weight.count_nonzero().item(),0)
        with self.assertRaises(ValueError):load_policy(target,{k:v for k,v in weights.items() if k!='value.weight'})

    def test_cached_ragged_rollout_keeps_actions_and_updates_on_cpu(self):
        torch.manual_seed(9);model=Policy(287,20)
        transitions=[]
        for t in range(3):
            obs=np.zeros((4,288+12*(t+1)),np.float32);obs[:,287]=t+1;obs[:,288:]=.2
            actions=[]
            for j in range(4):
                action=np.zeros((j+t+1,ACTION_WIDTH),np.float32);action[:,0]=1;actions.append(action)
            features,mask=padded(actions,'cpu')
            with torch.no_grad():dist,values=model(torch.as_tensor(obs),features,mask)
            selected=np.zeros(4,np.int64)
            transitions.append((obs,actions,selected,dist.log_prob(torch.as_tensor(selected)).detach().numpy(),values.numpy(),np.zeros(4),np.zeros(4)))
        advantage=np.ones((3,4),np.float32);returns=np.full((3,4),.5,np.float32)
        data=pack_rollout(transitions,advantage,returns,2,4,'cpu')
        self.assertEqual(data[2].sum(1).tolist(),[3,4,5])
        self.assertEqual(data[0][:,287].tolist(),[1,2,3])
        before=model.value.weight.detach().clone()
        metrics=optimize(model,optimizer_for(model,'cpu'),data,dict(minibatch=2,epochs=2),.002)
        self.assertTrue(np.isfinite(metrics['loss']));self.assertFalse(torch.equal(before,model.value.weight))

    def test_resume_accepts_original_limit_and_retains_benchmarked_environment_count(self):
        saved=dict(environments=64,requested_environments=256)
        self.assertEqual(resume_environment_count(256,saved),64)
        self.assertEqual(resume_environment_count(64,saved),64)
        with self.assertRaises(ValueError):resume_environment_count(128,saved)

    def test_ragged_visible_state_tokens_survive_padding_and_change_values(self):
        torch.manual_seed(7)
        first=np.zeros(287+1+12,np.float32);first[287]=1;first[288:]=.1
        second=np.zeros(287+1+24,np.float32);second[287]=2;second[288:]=.5
        observations=pad_observations([first,second])
        self.assertEqual(observations.shape,(2,len(second)))
        self.assertTrue(np.array_equal(observations[0,:len(first)],first))
        model=Policy(287,20);features,mask=padded([np.zeros((2,ACTION_WIDTH),np.float32)]*2,'cpu')
        _,values=model(torch.as_tensor(observations),features,mask)
        self.assertTrue(torch.isfinite(values).all());self.assertNotEqual(values[0].item(),values[1].item())
    @unittest.skipUnless(torch.cuda.is_available(),'CUDA unavailable')
    def test_cuda_executes_masked_policy_and_optimizer_update(self):
        model=Policy(287,20).cuda();optimizer=torch.optim.Adam(model.parameters(),lr=3e-4)
        features,mask=padded([np.zeros((103,ACTION_WIDTH),np.float32)],'cuda')
        distribution,value=model(torch.zeros(1,287,device='cuda'),features,mask)
        loss=value.mean()-distribution.entropy().mean();loss.backward();optimizer.step()
        self.assertTrue(torch.isfinite(loss));self.assertEqual(distribution.probs.shape[1],103)
    def test_auto_resume_retains_saved_device_while_explicit_override_is_respected(self):
        self.assertEqual(training_device('auto','cpu',{'resolved_device':'cuda'}),'cuda')
        self.assertEqual(training_device('cpu','cuda',{'resolved_device':'cuda'}),'cpu')
        self.assertEqual(training_device('auto','cuda'),'cuda')
    def test_perfect_small_samples_do_not_claim_zero_uncertainty(self):
        low,high=wilson(8,8)
        self.assertLess(low,1)
        self.assertAlmostEqual(high,1)
        self.assertEqual(wilson(0,0),[0,1])
    def test_ragged_masks_retain_every_legal_action(self):
        inputs=[np.zeros((n,ACTION_WIDTH),np.float32) for n in [1,7,103]]
        features,mask=padded(inputs,'cpu')
        model=Policy(287,20)
        distribution,value=model(torch.zeros(3,287),features,mask)
        self.assertEqual(mask.sum().item(),111)
        self.assertTrue(torch.isfinite(value).all())
        for _ in range(20):
            samples=distribution.sample().tolist()
            self.assertTrue(all(0<=sample<len(inputs[i]) for i,sample in enumerate(samples)))
        self.assertEqual(distribution.probs[0,1:].sum().item(),0)
    def test_terminal_rewards_do_not_bootstrap_from_reset_episode(self):
        rewards=np.array([[0,0],[1,0]],np.float32)
        terminals=np.array([[0,0],[1,0]],np.float32)
        values=np.zeros_like(rewards)
        advantage,returns=advantages(rewards,terminals,values,np.array([100,2],np.float32))
        self.assertEqual(returns[1,0],1)
        self.assertAlmostEqual(returns[0,0],.95)
        self.assertEqual(returns[1,1],2)

if __name__=='__main__': unittest.main()
