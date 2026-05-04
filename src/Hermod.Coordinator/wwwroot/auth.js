// Browser-side POST helper for the login flow.
//
// Why this exists: Login.razor used to call the AuthProxy via a
// server-side HttpClient. That call returned the Set-Cookie header
// to the SERVER's HttpClient, never to the user's browser, so the
// hermod_token / hermod_session cookies never landed and the next
// page request bounced back to /login. By doing the POST in the
// browser, the response Set-Cookies attach naturally and the
// follow-up navigation carries them.
window.hermodAuth = {
    async postJson(url, payload) {
        const response = await fetch(url, {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            credentials: "same-origin",
            body: JSON.stringify(payload)
        });
        const text = await response.text();
        return { status: response.status, body: text };
    }
};
