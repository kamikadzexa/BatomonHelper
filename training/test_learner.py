import unittest
import numpy as np
import torch
from learner import Policy, padded, pad_observations, ACTION_WIDTH, resume_environment_count, advantages, wilson, training_device

class LearnerTests(unittest.TestCase):
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
