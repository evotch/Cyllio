#!/usr/bin/env python3
"""Select application deployment without disabling existing services on main pushes."""
import json
import os
from pathlib import Path
import sys


def prepare(outputs, event, requested_services, account_secret_arn, kms_key_arn, sha):
    deployed = outputs.get('services_enabled', {}).get('value', False)
    enabled = deployed if event == 'push' else requested_services
    if deployed and not enabled:
        raise ValueError('Services are already deployed. Select deploy_services to preserve them.')
    if enabled and not account_secret_arn:
        raise ValueError('Set ACCOUNT_SECRET_ARN for account signing and phone-hash keys')
    return {
        'enable_services': enabled,
        'image_tag': sha if enabled else '',
        'account_secret_arn': account_secret_arn or None,
        'account_secret_kms_key_arn': kms_key_arn or None,
    }


def main():
    outputs = json.loads(Path(sys.argv[1]).read_text())
    try:
        values = prepare(
            outputs, os.environ['GITHUB_EVENT_NAME'],
            os.environ.get('REQUESTED_DEPLOY_SERVICES') == 'true',
            os.environ.get('ACCOUNT_SECRET_ARN', ''),
            os.environ.get('ACCOUNT_SECRET_KMS_KEY_ARN', ''),
            os.environ['GITHUB_SHA'],
        )
    except ValueError as error:
        raise SystemExit(str(error))
    Path('deployment.auto.tfvars.json').write_text(json.dumps(values))
    with open(os.environ['GITHUB_ENV'], 'a') as handle:
        handle.write('DEPLOY_SERVICES=' + str(values['enable_services']).lower() + '\n')


if __name__ == '__main__':
    main()
