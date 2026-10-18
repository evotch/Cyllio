import importlib.util
from pathlib import Path
import unittest

spec = importlib.util.spec_from_file_location(
    'deployment', Path(__file__).resolve().parents[2] / 'scripts/prepare-aws-deployment.py')
deployment = importlib.util.module_from_spec(spec)
spec.loader.exec_module(deployment)


class DeploymentSelectionTests(unittest.TestCase):
    def test_first_main_push_applies_foundation_without_account_secret(self):
        values = deployment.prepare({}, 'push', False, '', '', 'commit')
        self.assertFalse(values['enable_services'])
        self.assertEqual(values['image_tag'], '')
        self.assertIsNone(values['account_secret_arn'])

    def test_main_push_preserves_enabled_services_and_deploys_commit(self):
        values = deployment.prepare({'services_enabled': {'value': True}},
                                    'push', False, 'secret-arn', 'kms-arn', 'commit')
        self.assertTrue(values['enable_services'])
        self.assertEqual(values['image_tag'], 'commit')
        self.assertEqual(values['account_secret_kms_key_arn'], 'kms-arn')

    def test_manual_run_can_enable_services_after_foundation(self):
        values = deployment.prepare({'services_enabled': {'value': False}},
                                    'workflow_dispatch', True, 'secret-arn', '', 'commit')
        self.assertTrue(values['enable_services'])

    def test_manual_run_cannot_accidentally_remove_services(self):
        with self.assertRaisesRegex(ValueError, 'preserve'):
            deployment.prepare({'services_enabled': {'value': True}},
                               'workflow_dispatch', False, 'secret-arn', '', 'commit')

    def test_enabled_services_require_compatibility_secret(self):
        with self.assertRaisesRegex(ValueError, 'ACCOUNT_SECRET_ARN'):
            deployment.prepare({'services_enabled': {'value': True}},
                               'push', False, '', '', 'commit')


if __name__ == '__main__':
    unittest.main()
