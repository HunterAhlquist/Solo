// Presents Apple's web authentication sheet (ASWebAuthenticationSession) over
// a Solo window. Google refuses to sign in inside Electron, but this sheet is
// Safari (or a default browser that supports it) running as the system's
// sign-in UI: it is allowed, and it already has the user's Google cookies.
//
// Node-API only, so one build loads in any Electron version. Everything runs
// on the main thread, which is where Electron's main process runs JS.
//
//   start(url, callbackScheme, nativeWindowHandle, done) -> boolean
//   cancel()
//
// done(error, callbackUrl) fires once: callbackUrl when the page navigated to
// callbackScheme (the sheet closes itself), error "cancelled" when the user
// dismissed it or cancel() ran, any other error string otherwise.

#import <AppKit/AppKit.h>
#import <AuthenticationServices/AuthenticationServices.h>
#include <node_api.h>
#include <string>

@interface SoloAuthAnchor : NSObject <ASWebAuthenticationPresentationContextProviding>
@property(nonatomic, weak) NSWindow *window;
@end

@implementation SoloAuthAnchor
- (ASPresentationAnchor)presentationAnchorForWebAuthenticationSession:(ASWebAuthenticationSession *)session {
    return self.window;
}
@end

namespace {

ASWebAuthenticationSession *current = nil;
SoloAuthAnchor *anchor = nil;

struct Outcome {
    std::string error;
    std::string url;
};

void CallDone(napi_env env, napi_value callback, void *, void *data) {
    Outcome *outcome = static_cast<Outcome *>(data);
    if (env != nullptr && callback != nullptr) {
        napi_value args[2];
        if (outcome->error.empty()) {
            napi_get_null(env, &args[0]);
        } else {
            napi_create_string_utf8(env, outcome->error.c_str(), NAPI_AUTO_LENGTH, &args[0]);
        }
        if (outcome->url.empty()) {
            napi_get_null(env, &args[1]);
        } else {
            napi_create_string_utf8(env, outcome->url.c_str(), NAPI_AUTO_LENGTH, &args[1]);
        }
        napi_value global;
        napi_get_global(env, &global);
        napi_call_function(env, global, callback, 2, args, nullptr);
    }
    delete outcome;
}

std::string StringArg(napi_env env, napi_value value) {
    size_t length = 0;
    if (napi_get_value_string_utf8(env, value, nullptr, 0, &length) != napi_ok) {
        return {};
    }
    std::string result(length, '\0');
    napi_get_value_string_utf8(env, value, result.data(), length + 1, &length);
    return result;
}

napi_value Boolean(napi_env env, bool value) {
    napi_value result;
    napi_get_boolean(env, value, &result);
    return result;
}

napi_value Start(napi_env env, napi_callback_info info) {
    size_t argc = 4;
    napi_value argv[4];
    napi_get_cb_info(env, info, &argc, argv, nullptr, nullptr);
    if (argc < 4) {
        napi_throw_type_error(env, nullptr, "start(url, callbackScheme, nativeWindowHandle, done)");
        return nullptr;
    }

    NSURL *url = [NSURL URLWithString:[NSString stringWithUTF8String:StringArg(env, argv[0]).c_str()]];
    NSString *scheme = [NSString stringWithUTF8String:StringArg(env, argv[1]).c_str()];

    // getNativeWindowHandle() on macOS is a Buffer holding the NSView pointer.
    void *data = nullptr;
    size_t length = 0;
    if (napi_get_buffer_info(env, argv[2], &data, &length) != napi_ok || length < sizeof(void *)) {
        napi_throw_type_error(env, nullptr, "nativeWindowHandle must be a BrowserWindow handle");
        return nullptr;
    }
    NSView *view = (__bridge NSView *)(*static_cast<void **>(data));
    if (url == nil || view.window == nil) {
        return Boolean(env, false);
    }

    // One sheet at a time: a new sign-in replaces any that is still open.
    [current cancel];

    napi_value name;
    napi_create_string_utf8(env, "solo-auth-session", NAPI_AUTO_LENGTH, &name);
    napi_threadsafe_function done;
    if (napi_create_threadsafe_function(env, argv[3], nullptr, name, 0, 1, nullptr, nullptr,
                                        nullptr, CallDone, &done) != napi_ok) {
        return Boolean(env, false);
    }

    __block ASWebAuthenticationSession *session = nil;
    session = [[ASWebAuthenticationSession alloc]
              initWithURL:url
        callbackURLScheme:scheme
        completionHandler:^(NSURL *callbackURL, NSError *error) {
            Outcome *outcome = new Outcome();
            if (callbackURL != nil) {
                outcome->url = callbackURL.absoluteString.UTF8String;
            } else if (error.code == ASWebAuthenticationSessionErrorCodeCanceledLogin) {
                outcome->error = "cancelled";
            } else {
                outcome->error = (error.localizedDescription ?: @"unknown error").UTF8String;
            }
            napi_call_threadsafe_function(done, outcome, napi_tsfn_nonblocking);
            napi_release_threadsafe_function(done, napi_tsfn_release);
            if (current == session) {
                current = nil;
            }
        }];

    anchor = [SoloAuthAnchor new];
    anchor.window = view.window;
    session.presentationContextProvider = anchor;
    // Shared, not ephemeral: reusing the browser's existing Google sign-in is
    // the point of the sheet.
    session.prefersEphemeralWebBrowserSession = NO;

    if (![session start]) {
        napi_release_threadsafe_function(done, napi_tsfn_release);
        return Boolean(env, false);
    }
    current = session;
    return Boolean(env, true);
}

napi_value Cancel(napi_env env, napi_callback_info) {
    [current cancel];
    return nullptr;
}

napi_value Init(napi_env env, napi_value exports) {
    napi_property_descriptor properties[] = {
        {"start", nullptr, Start, nullptr, nullptr, nullptr, napi_default, nullptr},
        {"cancel", nullptr, Cancel, nullptr, nullptr, nullptr, napi_default, nullptr},
    };
    napi_define_properties(env, exports, 2, properties);
    return exports;
}

}  // namespace

NAPI_MODULE(NODE_GYP_MODULE_NAME, Init)
