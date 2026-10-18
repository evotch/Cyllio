#!/usr/bin/env python3
"""Integration checks against the Compose API, Identity and Device services."""
import base64
from concurrent.futures import ThreadPoolExecutor
import json
import os
import http.client
import secrets
import subprocess
import time
import urllib.error
import urllib.request

API = os.environ.get('TEST_API_URL', 'http://localhost:8080')
IDENTITY = os.environ.get('TEST_IDENTITY_URL', 'http://localhost:8081')


def call(method, path, body=None, token=None, etag=None, base=API):
    headers = {'Content-Type': 'application/json'}
    if token:
        headers['Authorization'] = 'Bearer ' + token
    if etag:
        headers['If-Match'] = etag
    request = urllib.request.Request(base + path, data=None if body is None else json.dumps(body).encode(), headers=headers, method=method)
    try:
        response = urllib.request.urlopen(request, timeout=20)
    except urllib.error.HTTPError as error:
        response = error
    payload = response.read()
    return response.status, (json.loads(payload) if "application/json" in response.headers.get("Content-Type", "") or "application/problem+json" in response.headers.get("Content-Type", "") else payload.decode()) if payload else None, response.headers


def expect(expected, result):
    assert result[0] == expected, f'Expected HTTP {expected}, got HTTP {result[0]}'
    return result


def verification(phone, installation):
    start = call('POST', '/api/v1/auth/verification/start', {'phoneNumber': phone})
    if start[0] == 429:
        time.sleep(31)
        start = call('POST', '/api/v1/auth/verification/start', {'phoneNumber': phone})
    started = expect(202, start)[1]
    code = expect(200, call('GET', '/dev/otp/' + started['verificationId'], base=IDENTITY))[1]['code']
    request = {'verificationId': started['verificationId'], 'code': code,
               'device': {'installationId': installation, 'platform': 'ios', 'appVersion': '1.0'}}
    expect(401, call('POST', '/api/v1/auth/verification/confirm', {**request, 'code': '000000'}))
    with ThreadPoolExecutor(max_workers=2) as pool:
        confirmations = list(pool.map(lambda _: call('POST', '/api/v1/auth/verification/confirm', request), range(2)))
    assert sorted(result[0] for result in confirmations) == [200, 401], 'A challenge may be confirmed once'
    confirmed = next(result[1] for result in confirmations if result[0] == 200)
    expect(401, call('POST', '/api/v1/auth/verification/confirm', request))
    return confirmed['registrationToken']


def register(phone, installation):
    proof = verification(phone, installation)
    expect(401, call('GET', '/api/v1/users/me', token=proof))
    expect(400, call('POST', '/api/v1/auth/register', {'displayName': 'Test', 'name': 'iPhone', 'identityKey': 'invalid'}, proof))
    body = {'displayName': 'Integration test', 'name': 'iPhone',
            'identityKey': base64.b64encode(secrets.token_bytes(32)).decode(),
            'signedPreKey': {'keyId': 1, 'publicKey': base64.b64encode(secrets.token_bytes(32)).decode(),
                             'signature': base64.b64encode(secrets.token_bytes(64)).decode()}}
    with ThreadPoolExecutor(max_workers=2) as pool:
        registrations = list(pool.map(lambda _: call('POST', '/api/v1/auth/register', body, proof), range(2)))
    assert sum(result[0] == 201 for result in registrations) == 1, 'A proof may create only one account'
    assert all(result[0] in (201, 401, 409) for result in registrations)
    account = next(result[1] for result in registrations if result[0] == 201)
    expect(401, call('POST', '/api/v1/auth/register', body, proof))
    return account


def main():
    for attempt in range(60):
        try:
            if call('GET', '/health/ready', base=IDENTITY)[0] == 200:
                break
        except (urllib.error.URLError, TimeoutError, http.client.RemoteDisconnected, ConnectionResetError):
            pass
        time.sleep(1)
    else:
        raise AssertionError('Identity service did not become ready')

    expect(401, call('GET', '/api/v1/users/me'))
    expect(404, call('GET', '/dev/otp/missing'))  # Gateway never exposes the development OTP route.
    expect(400, call('POST', '/api/v1/auth/verification/start', {'phoneNumber': 'invalid'}))
    installation = 'test-' + secrets.token_hex(8)
    phone_a = '+1555' + str(secrets.randbelow(10_000_000)).zfill(7)
    phone_b = '+1666' + str(secrets.randbelow(10_000_000)).zfill(7)
    a, b = register(phone_a, installation), register(phone_b, installation)
    assert a['userId'] != b['userId'] and a['deviceId'] != b['deviceId']
    token_a, token_b = a['accessToken'], b['accessToken']
    expect(429, call('POST', '/api/v1/auth/verification/start', {'phoneNumber': phone_a}))
    for account in [a, b]:
        devices = expect(200, call('GET', '/api/v1/devices', token=account['accessToken']))[1]['devices']
        assert len(devices) == 1 and devices[0]['deviceId'] == account['deviceId']
    expect(404, call('DELETE', '/api/v1/devices/' + b['deviceId'], token=token_a))
    expect(404, call('PUT', '/api/v1/devices/' + b['deviceId'] + '/push-token', {'provider': 'apns', 'token': 'test-push'}, token_a))
    expect(204, call('PUT', '/api/v1/devices/' + a['deviceId'] + '/push-token', {'provider': 'apns', 'token': 'test-push'}, token_a))

    _, settings, headers = expect(200, call('GET', '/api/v1/privacy/settings', token=token_a))
    etag = headers['ETag']
    settings['messages']['from'] = 'nobody'
    expect(428, call('PUT', '/api/v1/privacy/settings', settings, token_a))
    invalid = {**settings, 'calls': {**settings['calls'], 'from': 'invalid'}}
    expect(400, call('PUT', '/api/v1/privacy/settings', invalid, token_a, etag))
    with ThreadPoolExecutor(max_workers=2) as pool:
        statuses = sorted(pool.map(lambda _: call('PUT', '/api/v1/privacy/settings', settings, token_a, etag)[0], range(2)))
    assert statuses == [200, 412], 'Concurrent privacy writes must reject the stale version'
    assert expect(200, call('GET', '/api/v1/privacy/settings', token=token_b))[1]['messages']['from'] == 'contacts'

    _, profile, headers = expect(200, call('GET', '/api/v1/users/me', token=token_a))
    assert profile['userId'] == a['userId']
    expect(200, call('PATCH', '/api/v1/users/me', {'displayName': 'Updated', 'about': 'Test'}, token_a, headers['ETag']))
    expect(412, call('PATCH', '/api/v1/users/me', {'displayName': 'Stale'}, token_a, headers['ETag']))
    assert expect(200, call('GET', '/api/v1/users/me', token=token_b))[1]['displayName'] == 'Integration test'

    with ThreadPoolExecutor(max_workers=2) as pool:
        results = list(pool.map(lambda _: call('POST', '/api/v1/auth/refresh', {'refreshToken': a['refreshToken']}), range(2)))
    assert sorted(result[0] for result in results) == [200, 401], f'A refresh token may be consumed once; statuses: {[result[0] for result in results]}'
    rotated = next(result[1] for result in results if result[0] == 200)
    expect(401, call('POST', '/api/v1/auth/refresh', {'refreshToken': a['refreshToken']}))
    expect(204, call('POST', '/api/v1/auth/logout', token=rotated['accessToken']))
    expect(401, call('GET', '/api/v1/users/me', token=rotated['accessToken']))
    expect(401, call('GET', '/api/v1/users/me', token=token_a))
    expect(401, call('POST', '/api/v1/auth/refresh', {'refreshToken': rotated['refreshToken']}))
    expect(200, call('GET', '/api/v1/users/me', token=token_b))

    # Device revocation immediately invalidates access and refresh tokens for that account.
    expect(204, call('DELETE', '/api/v1/devices/' + b['deviceId'], token=token_b))
    expect(401, call('GET', '/api/v1/users/me', token=token_b))
    expect(401, call('POST', '/api/v1/auth/refresh', {'refreshToken': b['refreshToken']}))
    # Login needs a fresh proof and can only reuse an existing device on this installation.
    proof = verification(phone_a, installation)
    expect(401, call('POST', '/api/v1/auth/login', {'deviceId': b['deviceId']}, proof))
    login = expect(200, call('POST', '/api/v1/auth/login', {'deviceId': a['deviceId']}, proof))[1]
    expect(401, call('POST', '/api/v1/auth/login', {'deviceId': a['deviceId']}, proof))
    expect(200, call('GET', '/api/v1/users/me', token=login['accessToken']))
    subprocess.run(['docker', 'compose', 'restart', 'dynamodb', 'identity', 'device'], check=True, stdout=subprocess.DEVNULL)
    for attempt in range(60):
        try:
            if call('GET', '/health/ready')[0] == 200:
                break
        except (urllib.error.URLError, TimeoutError, http.client.RemoteDisconnected, ConnectionResetError):
            pass
        time.sleep(1)
    else:
        raise AssertionError('Account services did not recover after database/service restart')
    persisted = expect(200, call('GET', '/api/v1/users/me', token=login['accessToken']))[1]
    assert persisted['displayName'] == 'Updated', 'Profile and sessions must survive database and service restart'
    assert expect(200, call('GET', '/api/v1/privacy/settings', token=login['accessToken']))[1]['messages']['from'] == 'nobody'
    expect(204, call('POST', '/api/v1/auth/logout-all', token=login['accessToken']))
    expect(401, call('GET', '/api/v1/users/me', token=login['accessToken']))
    expect(401, call('POST', '/api/v1/auth/refresh', {'refreshToken': login['refreshToken']}))
    # After service restart the IP limiter has a fresh window. Concurrent requests
    # for the same phone must reserve one cooldown slot, including across time buckets.
    phone_c = '+1777' + str(secrets.randbelow(10_000_000)).zfill(7)
    with ThreadPoolExecutor(max_workers=2) as pool:
        starts = list(pool.map(lambda _: call('POST', '/api/v1/auth/verification/start', {'phoneNumber': phone_c}), range(2)))
    assert sorted(result[0] for result in starts) == [202, 429], 'Only one concurrent SMS challenge may start'
    challenge_id = next(result[1]['verificationId'] for result in starts if result[0] == 202)
    code = expect(200, call('GET', '/dev/otp/' + challenge_id, base=IDENTITY))[1]['code']
    confirmation = {'verificationId': challenge_id, 'code': '000000',
                    'device': {'installationId': installation, 'platform': 'ios'}}
    with ThreadPoolExecutor(max_workers=6) as pool:
        failures = list(pool.map(lambda _: call('POST', '/api/v1/auth/verification/confirm', confirmation), range(6)))
    assert all(result[0] == 401 for result in failures)
    expect(401, call('POST', '/api/v1/auth/verification/confirm', {**confirmation, 'code': code}))
    print('Account integration passed: concurrent OTP cooldown/confirmation/attempt limits, registration proof replay, account isolation, privacy/profile concurrency, refresh rotation, login proof isolation, restart persistence, logout-all and device revocation.')


if __name__ == '__main__':
    main()
