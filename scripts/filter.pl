#!/usr/bin/perl -n

use strict;
use warnings;

our @patterns;

BEGIN {
    my $file_ending = qr/\@\@(\\main(\\[\w\.]+)*\\\d+)?\r$/;
    @patterns = (
        # we need to know all interesting elements, even if they exist only in checkedout directories, but we skip checkedout *versions* (incremental export)
	qr/CHECKEDOUT\r$/,
	# general directories
	qr/\\directory_not_wanted/, 
	qr/\.unloaded/,
	qr/ - Copy/,
	qr/lost\+found/,

	# files
	qr/\.ccexclude$file_ending/,
	qr/\.mkelem$file_ending/,
	qr/\.\w+\.user$file_ending/,
	qr/\.suo$file_ending/,
	qr/\.checkedout$file_ending/,
	qr/\.hijacked$file_ending/,
	qr/\.contrib$file_ending/,
	qr/\.keep$file_ending/);
}

my $skip = 0;

foreach my $pattern (@patterns) {
    if (/$pattern/) {
	$skip = 1;
	last;
    }
}

next if $skip;
next if (exists $ENV{CC2GIT_EXCLUDES} && $ENV{CC2GIT_EXCLUDES} ne "" && m!$ENV{CC2GIT_EXCLUDES}!);

#s/.*\\MyVob\\//;
print
