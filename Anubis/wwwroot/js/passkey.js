// <passkey-submit> custom element: runs the WebAuthn ceremony in the browser
// (fingerprint, face, security key) and posts the result with the parent form.
//
// Attributes:
//   operation     "Create" (add a passkey) or "Request" (sign in)
//   name          form field prefix; the result is posted as <name>.CredentialJson
//                 or <name>.Error
//   token-name / token-value   antiforgery header for the options request
//   data-msg-*    localized error texts (cancelled, exists, unsupported, server, failed)
//
// A submit button inside the same form with name="__passkeySubmit" starts it.

const browserSupportsPasskeys =
    typeof navigator.credentials !== 'undefined' &&
    typeof window.PublicKeyCredential !== 'undefined' &&
    typeof window.PublicKeyCredential.parseCreationOptionsFromJSON === 'function' &&
    typeof window.PublicKeyCredential.parseRequestOptionsFromJSON === 'function';

class ServerError extends Error {
    name = 'ServerError';
}

async function fetchOptions(url, headers, signal) {
    const response = await fetch(url, { method: 'POST', credentials: 'include', headers, signal });
    if (!response.ok) {
        console.error(`Passkey options request failed with status ${response.status}:`, await response.text());
        throw new ServerError();
    }
    return await response.json();
}

async function createCredential(headers, signal) {
    const json = await fetchOptions('/auth/passkey/creation-options', headers, signal);
    const options = PublicKeyCredential.parseCreationOptionsFromJSON(json);
    return await navigator.credentials.create({ publicKey: options, signal });
}

async function requestCredential(mediation, headers, signal) {
    const json = await fetchOptions('/auth/passkey/request-options', headers, signal);
    const options = PublicKeyCredential.parseRequestOptionsFromJSON(json);
    return await navigator.credentials.get({ publicKey: options, mediation, signal });
}

customElements.define('passkey-submit', class extends HTMLElement {
    static formAssociated = true;

    connectedCallback() {
        this.internals = this.attachInternals();
        this.operation = this.getAttribute('operation');
        this.name = this.getAttribute('name');
        this.headers = { [this.getAttribute('token-name')]: this.getAttribute('token-value') };

        this.onSubmit = (event) => {
            if (event.submitter?.name === '__passkeySubmit') {
                event.preventDefault();
                this.obtainAndSubmit(false);
            }
        };
        this.internals.form.addEventListener('submit', this.onSubmit);

        if (!browserSupportsPasskeys) {
            // No CSS for this: hide the passkey button, show the "not supported" note.
            const section = this.closest('[data-passkey-section]');
            section?.querySelectorAll('button[name="__passkeySubmit"]').forEach(button => { button.style.display = 'none'; });
            section?.querySelectorAll('[data-passkey-unsupported]').forEach(note => { note.hidden = false; });
            return;
        }

        this.tryAutofill();
    }

    disconnectedCallback() {
        this.internals.form?.removeEventListener('submit', this.onSubmit);
        this.abortController?.abort();
    }

    async obtainAndSubmit(conditional) {
        this.abortController?.abort();
        this.abortController = new AbortController();
        const signal = this.abortController.signal;
        const formData = new FormData();

        try {
            if (!browserSupportsPasskeys) {
                throw Object.assign(new Error(), { name: 'UnsupportedError' });
            }

            const credential = this.operation === 'Create'
                ? await createCredential(this.headers, signal)
                : await requestCredential(conditional ? 'conditional' : undefined, this.headers, signal);

            formData.append(`${this.name}.CredentialJson`, JSON.stringify(credential));
        } catch (error) {
            // Aborted on purpose (e.g. the user clicked the button while autofill was pending).
            if (error.name === 'AbortError') {
                return;
            }
            console.error(error);
            // Autofill is not started by the user, so don't show its errors.
            if (conditional) {
                return;
            }
            const messages = {
                NotAllowedError: this.dataset.msgCancelled,
                InvalidStateError: this.dataset.msgExists,
                UnsupportedError: this.dataset.msgUnsupported,
                ServerError: this.dataset.msgServer,
            };
            // Browser messages for other errors are not localized; the generic server text is.
            const message = messages[error.name] ?? this.dataset.msgFailed;
            formData.append(`${this.name}.Error`, message);
        }

        this.internals.setFormValue(formData);
        this.internals.form.submit();
    }

    // Offer passkeys in the browser's autofill list for fields marked
    // autocomplete="username webauthn".
    async tryAutofill() {
        if (this.operation === 'Request' && await PublicKeyCredential.isConditionalMediationAvailable?.()) {
            await this.obtainAndSubmit(true);
        }
    }
});
