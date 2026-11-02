// A real WebKit browser for the Browser demo. The WKWebView lives behind Unity's view, where it
// still renders; snapshots of it, about 20 a second, are copied into a buffer Unity uploads to the
// floating screen's texture. Input arrives as JavaScript (taps, scrolling, typed text).
// Everything runs on the main thread, which is also Unity's on iOS.

#import <UIKit/UIKit.h>
#import <WebKit/WebKit.h>

static WKWebView *s_Web;
static NSTimer *s_Timer;
static unsigned char *s_Pixels;
static int s_Width, s_Height;
static int s_Frame;
static BOOL s_Busy;

static UIWindow *GlassesWeb_Window(void)
{
    for (UIScene *scene in UIApplication.sharedApplication.connectedScenes)
    {
        if (![scene isKindOfClass:UIWindowScene.class])
            continue;
        for (UIWindow *window in ((UIWindowScene *)scene).windows)
            if (window.isKeyWindow)
                return window;
        if (((UIWindowScene *)scene).windows.count > 0)
            return ((UIWindowScene *)scene).windows.firstObject;
    }
    return nil;
}

static void GlassesWeb_Snapshot(void)
{
    if (s_Web == nil || s_Busy)
        return;
    s_Busy = YES;
    WKSnapshotConfiguration *config = [WKSnapshotConfiguration new];
    config.snapshotWidth = @(s_Width);
    [s_Web takeSnapshotWithConfiguration:config completionHandler:^(UIImage *image, NSError *error) {
        s_Busy = NO;
        if (image == nil || s_Pixels == NULL)
            return;
        CGColorSpaceRef space = CGColorSpaceCreateDeviceRGB();
        CGContextRef context = CGBitmapContextCreate(s_Pixels, s_Width, s_Height, 8, s_Width * 4, space,
            kCGImageAlphaPremultipliedLast | kCGBitmapByteOrder32Big);
        CGColorSpaceRelease(space);
        if (context == NULL)
            return;
        // Unity textures start at the bottom row; flip so the first row in memory is the bottom.
        CGContextTranslateCTM(context, 0, s_Height);
        CGContextScaleCTM(context, 1, -1);
        CGContextDrawImage(context, CGRectMake(0, 0, s_Width, s_Height), image.CGImage);
        CGContextRelease(context);
        s_Frame++;
    }];
}

extern "C" {

// Width and height in pixels of the picture, which are also the page's size in CSS pixels.
int GlassesWeb_Create(int width, int height)
{
    if (s_Web != nil)
        return 1;
    UIWindow *window = GlassesWeb_Window();
    if (window == nil)
        return 0;
    s_Width = width;
    s_Height = height;
    s_Pixels = (unsigned char *)calloc((size_t)width * height * 4, 1);
    s_Frame = 0;

    WKWebViewConfiguration *config = [WKWebViewConfiguration new];
    config.allowsInlineMediaPlayback = YES;
    config.mediaTypesRequiringUserActionForPlayback = WKAudiovisualMediaTypeNone;
    s_Web = [[WKWebView alloc] initWithFrame:CGRectMake(0, 0, width, height) configuration:config];
    s_Web.allowsBackForwardNavigationGestures = NO;
    // Behind Unity's view: hidden from the user, but still in a window, so it keeps rendering.
    [window insertSubview:s_Web atIndex:0];

    s_Timer = [NSTimer scheduledTimerWithTimeInterval:1.0 / 20.0 repeats:YES block:^(NSTimer *timer) {
        GlassesWeb_Snapshot();
    }];
    return 1;
}

void GlassesWeb_Load(const char *url)
{
    if (s_Web == nil || url == NULL)
        return;
    NSURL *address = [NSURL URLWithString:[NSString stringWithUTF8String:url]];
    if (address != nil)
        [s_Web loadRequest:[NSURLRequest requestWithURL:address]];
}

void GlassesWeb_Eval(const char *script)
{
    if (s_Web == nil || script == NULL)
        return;
    [s_Web evaluateJavaScript:[NSString stringWithUTF8String:script] completionHandler:nil];
}

void GlassesWeb_Back(void)
{
    if (s_Web.canGoBack)
        [s_Web goBack];
}

void GlassesWeb_Reload(void)
{
    [s_Web reload];
}

int GlassesWeb_IsLoading(void)
{
    return s_Web.isLoading ? 1 : 0;
}

// Copies the newest picture into buffer if it's newer than lastFrame; returns its number, or -1.
int GlassesWeb_CopyFrame(unsigned char *buffer, int length, int lastFrame)
{
    if (s_Pixels == NULL || s_Frame == lastFrame || length < s_Width * s_Height * 4)
        return -1;
    memcpy(buffer, s_Pixels, (size_t)s_Width * s_Height * 4);
    return s_Frame;
}

void GlassesWeb_Destroy(void)
{
    [s_Timer invalidate];
    s_Timer = nil;
    [s_Web stopLoading];
    [s_Web removeFromSuperview];
    s_Web = nil;
    free(s_Pixels);
    s_Pixels = NULL;
    s_Busy = NO;
}

}
