# Dedicated OTP short-code setup

Request separate short codes for **United States (US)** and **Bulgaria (BG)** in the AWS region used by Identity (`us-west-2` for the current deployment). Ask for a five-digit code if available; AWS/carriers determine availability and the assigned number. A US code is not a worldwide sender.

In AWS Console → Support Center, follow [Requesting dedicated short codes](https://docs.aws.amazon.com/sms-voice/latest/userguide/phone-numbers-request-short-code.html). Prepare one country-specific request with:

- Company legal name, registered address, public website and an authorized contact.
- Country, desired AWS region and use case: transactional phone-verification/login OTP for Cyllio.
- Expected messages per day/month and peak throughput; do not invent these estimates.
- Actual opt-in flow: user enters their phone number and explicitly requests a verification code. Include screenshots and the real privacy/terms URLs.
- Example message: `Your Cyllio verification code is 123456. It expires in 5 minutes. Do not share this code.`
- Any required HELP/STOP, support contact and opt-in/opt-out handling approved for that country.
- Required SMS production access, spending quota and short-code registration/carrier approval.

This is a preparation checklist; no support request or paid number reservation has been submitted automatically. Approval and fees are determined by AWS/carriers. After provisioning, retrieve each short code's **phone-number ARN**, then configure `sms_origination_identities.US` and `.BG` in Terraform. Use real verified recipients while the account is sandboxed. An approved production sender/production access is required for general sending.
