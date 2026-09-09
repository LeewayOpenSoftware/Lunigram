#include "pch.h"
#include "AutomaticDragHelper.h"
#if __has_include("Controls/AutomaticDragHelper.g.cpp")
#include "Controls/AutomaticDragHelper.g.cpp"
#endif

#include <winrt/Windows.Devices.Input.h>

using namespace winrt::Windows::Devices::Input;

namespace winrt::Telegram::Native::Controls::implementation
{
    AutomaticDragHelper::AutomaticDragHelper(const UIElement& pUIElement, bool shouldAddInputHandlers)
        : m_pOwnerNoRef(pUIElement)
        , m_shouldAddInputHandlers(shouldAddInputHandlers)
    {
    }

    // Begin tracking the mouse cursor in order to fire a drag start if the pointer
    // moves a certain distance away from m_lastMouseLeftButtonDownPosition.
    void AutomaticDragHelper::BeginCheckingForMouseDrag(const Pointer& pPointer)
    {
        m_isCheckingForMouseDrag = !!m_pOwnerNoRef.CapturePointer(pPointer);
    }


    // Stop tracking the mouse cursor.
    void AutomaticDragHelper::StopCheckingForMouseDrag(const Pointer& pPointer)
    {
        // Do not call ReleasePointerCapture() more times than we called CapturePointer()
        if (m_isCheckingForMouseDrag)
        {
            m_isCheckingForMouseDrag = false;

            m_pOwnerNoRef.ReleasePointerCapture(pPointer);
        }
    }

    // Return true if we're tracking the mouse and newMousePosition is outside the drag
    // rectangle centered at m_lastMouseLeftButtonDownPosition (see IsOutsideDragRectangle).
    bool AutomaticDragHelper::ShouldStartMouseDrag(Point newMousePosition)
    {
        return m_isCheckingForMouseDrag && IsOutsideDragRectangle(newMousePosition, m_lastMouseLeftButtonDownPosition);
    }

    // Returns true if testPoint is outside of the rectangle
    // defined by the SM_CXDRAG and SM_CYDRAG system metrics and
    // dragRectangleCenter.
    bool AutomaticDragHelper::IsOutsideDragRectangle(Point testPoint, Point dragRectangleCenter)
    {
        double dx = std::abs(testPoint.X - dragRectangleCenter.X);
        double dy = std::abs(testPoint.Y - dragRectangleCenter.Y);

        // TODO: GetSystemMetrics fails when compiling RELEASE
        double maxDx = 4; // GetSystemMetrics(SM_CXDRAG);
        double maxDy = 4; // GetSystemMetrics(SM_CYDRAG);

        maxDx *= UIELEMENT_MOUSE_DRAG_THRESHOLD_MULTIPLIER;
        maxDy *= UIELEMENT_MOUSE_DRAG_THRESHOLD_MULTIPLIER;

        return (dx > maxDx || dy > maxDy);
    }


    void AutomaticDragHelper::StartDetectingDrag()
    {
        if (m_shouldAddInputHandlers && !m_dragDropPointerPressedToken)
        {
            m_dragDropPointerPressedToken = AddRoutedEventHandler<RoutedEventType::PointerPressed>(m_pOwnerNoRef, { this, &AutomaticDragHelper::HandlePointerPressedEventArgs }, true);
        }
    }

    void AutomaticDragHelper::StopDetectingDrag()
    {
        if (m_dragDropPointerPressedToken)
        {
            m_dragDropPointerPressedToken.revoke();
        }
    }

    void AutomaticDragHelper::RegisterDragPointerEvents()
    {
        if (m_shouldAddInputHandlers)
        {
            // Hookup pointer events so we can catch and handle it for drag and drop.
            if (!m_dragDropPointerMovedToken)
            {
                m_dragDropPointerMovedToken = AddRoutedEventHandler<RoutedEventType::PointerMoved>(m_pOwnerNoRef, { this, &AutomaticDragHelper::HandlePointerMovedEventArgs }, true);
            }

            if (!m_dragDropPointerReleasedToken)
            {
                m_dragDropPointerReleasedToken = AddRoutedEventHandler<RoutedEventType::PointerReleased>(m_pOwnerNoRef, { this, &AutomaticDragHelper::HandlePointerReleasedEventArgs }, true);
            }

            if (!m_dragDropPointerCaptureLostToken)
            {
                m_dragDropPointerCaptureLostToken = AddRoutedEventHandler<RoutedEventType::PointerCaptureLost>(m_pOwnerNoRef, { this, &AutomaticDragHelper::HandlePointerCaptureLostEventArgs }, true);
            }
        }
    }

    void AutomaticDragHelper::HandlePointerPressedEventArgs(const IInspectable& sender, const PointerRoutedEventArgs& args)
    {
        auto spPointer = args.Pointer();
        auto pointerDeviceType = spPointer.PointerDeviceType();

        auto spPointerPoint = args.GetCurrentPoint(m_pOwnerNoRef);

        // Check if this is a mouse button down.
        if (pointerDeviceType == PointerDeviceType::Mouse || pointerDeviceType == PointerDeviceType::Pen)
        {
            // Mouse button down.
            auto isLeftButtonPressed = spPointerPoint.Properties().IsLeftButtonPressed();

            // If the left mouse button was the one pressed...
            if (!m_isLeftButtonPressed && isLeftButtonPressed)
            {
                m_isLeftButtonPressed = true;
                // Start listening for a mouse drag gesture
                m_lastMouseLeftButtonDownPosition = spPointerPoint.Position();
                BeginCheckingForMouseDrag(spPointer);

                RegisterDragPointerEvents();
            }
        }
    }

    void AutomaticDragHelper::HandlePointerMovedEventArgs(const IInspectable& sender, const PointerRoutedEventArgs& args)
    {
        auto spPointer = args.Pointer();
        auto pointerDeviceType = spPointer.PointerDeviceType();

        // Our behavior is different between mouse and touch.
        // It's up to us to detect mouse drag gestures - if we
        // detect one here, start a drag drop.
        if (pointerDeviceType == PointerDeviceType::Mouse || pointerDeviceType == PointerDeviceType::Pen)
        {
            auto spPointerPoint = args.GetCurrentPoint(m_pOwnerNoRef);

            auto newMousePosition = spPointerPoint.Position();
            if (ShouldStartMouseDrag(newMousePosition))
            {
                StopCheckingForMouseDrag(spPointer);

                try
                {
                    m_pOwnerNoRef.StartDragAsync(spPointerPoint);
                }
                catch (...)
                {
                    // All the remote procedure calls must be wrapped in a try-catch block
                }
            }
        }
    }

    void AutomaticDragHelper::HandlePointerReleasedEventArgs(const IInspectable& sender, const PointerRoutedEventArgs& args)
    {
        auto spPointer = args.Pointer();
        auto pointerDeviceType = spPointer.PointerDeviceType();

        // Check if this is a mouse button up
        if (pointerDeviceType == PointerDeviceType::Mouse || pointerDeviceType == PointerDeviceType::Pen)
        {
            auto spPointerPoint = args.GetCurrentPoint(m_pOwnerNoRef);
            auto spPointerProperties = spPointerPoint.Properties();
            auto isLeftButtonPressed = spPointerProperties.IsLeftButtonPressed();

            // if the mouse left button was the one released...
            if (m_isLeftButtonPressed && !isLeftButtonPressed)
            {
                m_isLeftButtonPressed = false;
                UnregisterEvents();
                // Terminate any mouse drag gesture tracking.
                StopCheckingForMouseDrag(spPointer);
            }
        }
        else
        {
            UnregisterEvents();
        }
    }

    void AutomaticDragHelper::HandlePointerCaptureLostEventArgs(const IInspectable& sender, const PointerRoutedEventArgs& args)
    {
        auto spPointer = args.Pointer();
        auto pointerDeviceType = spPointer.PointerDeviceType();

        if (pointerDeviceType == PointerDeviceType::Mouse || pointerDeviceType == PointerDeviceType::Pen)
        {
            // We're not necessarily going to get a PointerReleased on capture lost, so reset this flag here.
            m_isLeftButtonPressed = false;
        }

        UnregisterEvents();
    }

    void AutomaticDragHelper::UnregisterEvents()
    {
        // Unregister events handlers
        if (m_dragDropPointerMovedToken)
        {
            m_dragDropPointerMovedToken.revoke();
        }

        if (m_dragDropPointerReleasedToken)
        {
            m_dragDropPointerReleasedToken.revoke();
        }

        if (m_dragDropPointerCaptureLostToken)
        {
            m_dragDropPointerCaptureLostToken.revoke();
        }
    }
}
