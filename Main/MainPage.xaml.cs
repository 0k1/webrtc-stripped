//*********************************************************
//
// Copyright (c) Microsoft. All rights reserved.
// This code is licensed under the MIT License (MIT).
// THIS CODE IS PROVIDED *AS IS* WITHOUT WARRANTY OF
// ANY KIND, EITHER EXPRESS OR IMPLIED, INCLUDING ANY
// IMPLIED WARRANTIES OF FITNESS FOR A PARTICULAR
// PURPOSE, MERCHANTABILITY, OR NON-INFRINGEMENT.
//
//*********************************************************

using PeerConnectionClient.ViewModels;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace PeerConnectionClient
{
    /// <summary>
    /// The application main page.
    /// </summary>
    public sealed partial class MainPage : Page
    {
        private MainViewModel _mainViewModel;

        /// <summary>
        /// Constructor.
        /// </summary>
        public MainPage()
        {
            this.InitializeComponent();
        }

        /// <summary>
        /// See Page.OnNavigatedTo()
        /// </summary>
        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            _mainViewModel = (MainViewModel)e.Parameter;
            this.DataContext = _mainViewModel;
        }
    }
}