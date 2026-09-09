#include "pch.h"
#include "MediaChannelDescriptionsRequestedEventArgs.h"

namespace winrt::Telegram::Native::Calls::implementation
{
    MediaChannelDescriptionsRequestedEventArgs::MediaChannelDescriptionsRequestedEventArgs(IVector<uint32_t> audioSourceIds, MediaChannelDescriptionsRequestedDeferral deferral)
        : m_audioSourceIds(audioSourceIds)
        , m_deferral(deferral)
    {

    }

    IVector<uint32_t> MediaChannelDescriptionsRequestedEventArgs::AudioSourceIds()
    {
        return m_audioSourceIds;
    }

    MediaChannelDescriptionsRequestedDeferral MediaChannelDescriptionsRequestedEventArgs::Deferral()
    {
        return m_deferral;
    }
}
